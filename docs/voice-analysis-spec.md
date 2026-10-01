# voice analysis prototype — technical spec v2.5

phase 0 deliverable. the goal is not a game. the goal is to answer "is the tracking good enough to build on?" with numbers, before any engine work happens.

v2 incorporates the Sol review plus a second pass. v2.3 applies a fresh-eyes review done before any code was written: a breathy voicing path, a fixed-window YIN difference function, calibrated confidence, a frame queue, a precise capture-to-result definition, and resonance cross-talk validation. changes from v1 are summarized at the end (§9).

---

## 0. product boundary (read this first)

VoiceCore **measures**. it never **interprets**.

- the analyzer reports pitch, formants, periodicity, level, and confidence. it does not classify a voice as masculine, feminine, androgynous, passing, successful, or failed. those words do not appear in its API, its logs, or its metrics.
- goals are chosen by the user in the game layer. the game maps measurements onto *user-selected* target ranges. the mapping lives in game code, is user-editable, and defaults to nothing.
- no single acoustic measurement (VTL least of all) is ever presented as a gender readout.

this is an architectural rule, not a tone note. it keeps the analyzer testable against physics while the product stays configurable against people's actual goals — masc, fem, androgynous, or "just less strain."

### privacy requirements (phase 0, not later)

- all processing is local. no audio leaves the machine, ever, by default.
- recording retention is opt-in with explicit consent per session; a visible indicator whenever audio is being persisted; one-click deletion of everything retained.
- no audio, no raw frames, and no derived voice measurements in telemetry or crash reports.
- the evaluation corpus requires documented consent and usage rights from every speaker, including the right to be removed. store consent records with the manifest.

---

## 1. solution layout

three projects. the split matters more than anything else in this doc.

```
VoiceCore/           class library (see TFM note below)
  - zero UI, zero audio-device code, zero platform deps
  - stateful streaming analyzer: push samples in, frames come out
  - this is the assembly Godot will reference later, unchanged

VoiceCore.Batch/     console app
  - runs VoiceCore over a corpus of WAVs *through the same streaming path*
  - emits per-frame Parquet + summary CSV

VoiceProbe/          minimal Godot 4 C# project
  - live mic capture, live pitch trace, frame logging
  - disposable. do not put logic here.

VoiceProbe.Capture/  class library the probe uses (build step 3)
  - PortAudio capture, device format policy + 44.1→48 kHz resampler (§3),
    loopback latency test, session log
  - no Godot dependency, so it's unit-testable; VoiceCore still never
    references it

VoiceCore.Synthetic/ test tooling (build step 2)
  - synthetic signals with exact ground truth, the §3.4 suites, and the
    harness that scores frames against them. never ships.

VoiceCore.Tests/     xUnit
```

**VoiceCore must never know what a microphone is.** it takes buffers. the probe and the game both feed it. that isolation is enforced by VoiceCore being its own assembly with no engine or device references. the probe's host framework doesn't affect it.

**why the probe is Godot (v2.3, was Avalonia):** the probe is disposable either way. but a Godot probe also:

- gets the HDR glow the game will need, so step 8's legibility test runs in the real renderer
- lets the phase 1a capture comparison (native PortAudio vs `AudioEffectCapture`) start in phase 0
- means one less UI framework to learn and then throw away

**TFM (v2.3):** .NET 8 LTS goes out of support in November 2026. target whatever the current Godot release supports for C# projects, preferring .NET 10 LTS. VoiceCore has no framework-specific dependencies, so multi-targeting is cheap if needed.

### 1.1 the analyzer contract

v1 said "pure function: float[] in, AnalysisFrame[] out." that's wrong — the analyzer needs persistent state (DC filter, overlapping windows, noise floor, octave-correction history, formant tracks) and an array-returning API allocates on every call. the real contract:

```csharp
public sealed class VoiceAnalyzer
{
    public VoiceAnalyzer(AnalysisConfig config);

    /// samples of delay between an acoustic event and the frame that
    /// reflects it, measured at the window center. fixed per config.
    public int AlgorithmicDelaySamples { get; }

    /// clears all temporal state (filters, medians, tracks, noise floor
    /// adaptation). call on stream start and after any capture gap.
    public void Reset(long nextSampleIndex = 0);

    /// upper bound on frames one Process call can emit for an input of
    /// this length: inputLength / HopSamples + 1.
    public static int MaxFramesFor(int inputLength);

    /// consumes input, writes any completed frames to output,
    /// returns the number written. zero-allocation in steady state.
    /// output.Length must be >= MaxFramesFor(input.Length); a smaller
    /// span throws ArgumentException (a caller bug, not a runtime state).
    public int Process(ReadOnlySpan<float> input, Span<AnalysisFrame> output);

    public AnalyzerDiagnostics Diagnostics { get; }  // counters, see §2
}
```

**chunk-boundary invariance is a contract, and a test:** feeding the same signal in chunks of 1, 480, 4096, or random sizes must produce bit-identical frames. batch mode and Godot use this exact path — there is no separate offline code path for the causal analyzer. (offline *scoring* may additionally run centered filters; see §3.4.)

`AnalysisConfig` is immutable; changing parameters means constructing a new analyzer. the config carries an `AnalyzerVersion` string and a content hash, stamped into every Parquet file and log header (not into every frame).

### 1.2 dependencies

| package | project | license |
|---|---|---|
| PortAudioSharp2 | VoiceProbe.Capture | MIT |
| FftSharp | VoiceCore | MIT |
| Parquet.Net | VoiceCore.Batch | Apache-2.0 |
| Godot 4 (.NET) | VoiceProbe | MIT |

all permissive. caveat: FftSharp's public API allocates per call. either wrap it with pooled buffers behind an internal `IFftPlan` interface, or hand-roll the real FFT (~100 lines).

**LPC root finding is hand-rolled (v2.3).** v2 listed MathNet.Numerics for companion-matrix eigenvalues. its eigen solver allocates on every call, which breaks the zero-allocation contract in §1.1. the polynomial is at most order 18 (`maxFormantHz` = 8000), so a small solver on preallocated buffers is enough: Hessenberg QR on the companion matrix, or Aberth–Ehrlich on the polynomial. MathNet may stay in `VoiceCore.Tests` as the reference to check roots against.

---

## 2. threading and buffering

```
capture callback (realtime thread)
    └─> lock-free SPSC ring buffer (2^17 floats ≈ 2.7 s @ 48 kHz)
            └─> analysis thread (dedicated, not thread pool)
                    ├─> SPSC frame queue (every frame, 256 slots ≈ 2.5 s)
                    │       └─> trace renderer, scoring, logging
                    └─> triple-buffered latest AnalysisFrame
                            └─> meters/needles read without locking
```

**every frame reaches its consumers (v2.3).** v2 only had the triple buffer, which is right for a needle meter but drops frames by design: 100 frames/s against a 60 fps render. a pitch trace needs every frame, or it misses short events and draws a subsampled line. scoring needs every frame, or grades depend on render timing. so the frame queue is the primary output, and the triple buffer is a convenience for "what's the value right now" displays. if the queue overflows because the consumer stalls, drop the oldest frames and increment `Diagnostics.FrameQueueOverruns`. the consumer treats the gap like a capture gap: it scores nothing across it.

rules for the capture callback: no allocation, no locking, no logging. write and return.

ring buffer: power-of-two size, `Interlocked` read/write indices, mask instead of modulo.

**overflow policy (new in v2):** a 2.7 s buffer must never become 2.7 s of stale feedback. if the analysis thread falls behind:

- live mode **drops the oldest audio** to bring backlog under 100 ms,
- calls `Reset()` on the analyzer (temporal state is invalid across the gap),
- increments `Diagnostics.OverrunCount` and `Diagnostics.DroppedSamples`.

batch mode never drops; it just runs slower than realtime.

`AnalyzerDiagnostics`: overrun count, dropped samples, frame-queue overruns, frames produced, max analysis time per frame. logged per session; overruns > 0 on target hardware is a bug.

---

## 3. signal chain

internal processing contract: **48 kHz, mono, float32, normalized −1..1**.

**device format policy (new in v2):** 44.1 kHz devices are common and must work. the probe (not VoiceCore) converts: resample 44.1→48 kHz with a polyphase FIR (spec its stopband ≥ 80 dB), downmix stereo by taking the left channel (not averaging — averaging two mics in unknown phase can comb-filter). unsupported formats produce an explicit error listing the device's native formats, never silent garbage. the session log records: device name, native format, OS, and whether OS-level processing (AGC, noise suppression, "voice isolation") could be disabled. **request the raw/unprocessed stream wherever the platform allows it** — OS noise suppression will eat exactly the low-energy periodicity we're trying to measure.

hop size **480 samples (10 ms)**. every stage reports on this grid.

### 3.1 timing model (new in v2 — read before implementing anything)

every frame carries two timestamps:

- `WindowCenterSample` — the center of the analysis window. all measurements are *about* this moment.
- `ResultAvailableSample` — the capture-clock sample count at which the frame could first exist (end of the window; filter delays included).

derived definitions, used consistently everywhere:

- **algorithmic delay** = `ResultAvailableSample − WindowCenterSample`. for the 2048-sample YIN window: 1024 samples ≈ 21.3 ms, plus the FIR group delays in the formant path (compensated in timestamps, see §3.6).
- **capture-to-result latency** = wall-clock from the arrival in the ring buffer of the **last sample the frame depends on** (the sample at `ResultAvailableSample`) to the frame being readable by its consumers. this is analysis compute plus thread scheduling. it does not include algorithmic delay, which is a fixed constant per config and is reported separately. (v2.3: v2 said "a sample … its frame." measured from the window center, the same frame reads ~21 ms slower than measured from its last sample, and Gate B's threshold sat right in that gap.)
- **user-to-photon latency** = mouth to pixels. includes device/driver buffering, capture-to-result, render queuing, and display scanout. **only measurable end-to-end** — an audio loopback test does not capture the render half. measure with a clap-to-screen-flash camera test (240 fps phone camera is sufficient).

v1's latency table mixed these. the budget in §5 uses the definitions above.

### 3.2 preprocessing

- **DC removal**: one-pole high-pass, `y[n] = x[n] - x[n-1] + 0.995 * y[n-1]` (fc ≈ 38 Hz @ 48 kHz — below the 60 Hz search floor, fine)
- **no AGC on the analysis path.** if a monitoring path wants AGC, it's a separate branch.
- **RMS** over the hop → dBFS
- **peak** over the hop → dBFS, plus a clipping flag at > −0.1 dBFS
- **noise floor**: measured during an explicit calibration step (2 s of instructed silence → 10th percentile of hop RMS), persisted. during use, adapt slowly (time constant ~10 s) but **only during frames already classified Silence** — never let speech drag the floor up. v1 said both "rolling 5 s" and "captured during calibration"; this replaces both.
- **device-side processing shows up in calibration** (v2.5, found on a webcam mic). The Insta360 Link 2C Pro delivered *exact digital zero* during instructed silence, even with WASAPI raw mode requested. That means the device runs its own noise suppression or gate; it probably also applies gain control, since it was ~15 dB louder than a condenser at a greater distance. The calibrated floor then clamps to −100 dBFS and the level gate stops gating. Calibration must detect this (a silence window that's all zeros, or a floor at the clamp) and report "this mic processes its audio" to the player, and the session log must record it. What the gate should do instead is open: candidates are the 10th percentile of non-zero hops, or a fixed floor for such devices. It needs corpus evidence from more processed mics, and it's tracked as a step 6+ item.

### 3.3 voicing decision — explicit order (rewritten in v2)

v1 had a contradiction: the voiced gate required aperiodicity < 0.20, but creak required aperiodicity > 0.30 — creaky frames would be filed as Unvoiced before the creak rule ever ran. v1's creak rule also depended on `jitterLocal`, which phase 0 cannot validly compute (§3.8). fixed decision order:

```
1. LEVEL GATE (calibratable, default: RMS > noiseFloor + 8 dB → "has energy")
   below it → Silence. note the default is 8 dB, not v1's 12: soft breathy
   phonation is a *core practice mode* for this audience, and it sits close
   to the floor on laptop mics. the corpus must include soft phonation and
   the gate threshold is tuned against it.

2. F0 CANDIDATE — always. YIN runs on every non-silent frame and emits an
   internal candidate {f0RawHz, aperiodicity}, even when confidence is low.
   the candidate is always logged. it is *published* as F0Hz only when
   step 3 lands on Voiced.

3. CLASSIFY on {aperiodicity, f0 stability, ZCR, temporal evidence}:
     aperiodicity < 0.20                                → Voiced
     0.20 ≤ aperiodicity < 0.45, f0 candidate STABLE    → Voiced (breathy path)
     0.20 ≤ aperiodicity < 0.45, not STABLE, low ZCR    → Creak candidate
     else                                                → Unvoiced

   BREATHY PATH (new in v2.3). breathy phonation is a periodic source plus
   aspiration noise, so its aperiodicity can sit well above 0.20. it is
   also sustained, so v2's rules would have filed it as Creak (3 of 5
   frames) or Unvoiced. that blanks the pitch line during a core practice
   mode, the same case the +8 dB level gate was lowered for. what separates
   breathy voice from creak is that its period stays steady:
     STABLE = f0RawHz within ±50 cents of the median of the previous 3
     non-silent candidates, all inside the search range. (tolerates
     glides up to ~2500 cents/s.)
   breathy-path frames publish as Voiced with F0Confidence lowered by
   their aperiodicity (§3.9). a frame at onset has no candidate history,
   so it can't be STABLE. a breathy onset therefore publishes Unvoiced for
   ~30 ms, about the same delay as creak's multi-frame rule.

   ZCR = sign changes per second over the current hop, on the DC-removed
   48 kHz signal. "low" defaults to < 2000/s, a starting guess to be tuned
   on the corpus.

   CREAK IS A MULTI-FRAME CALL, not a single-frame threshold: a creak
   candidate is published as Creak only when ≥3 of the last 5 non-silent
   frames are creak candidates. until then it is published as Unvoiced
   with the candidate logged.

   SUBHARMONIC EVIDENCE (corrected in v2.3). v2 counted "d′(2τ)
   competitive with d′(τ)" as creak evidence. but for any clean periodic
   signal d′(2τ) ≈ d′(τ) ≈ 0, so that test fires on ordinary voiced
   speech. period doubling (alternate cycles differ) does show up as a
   moderate d′ dip at half the best lag. a modal voice with a strong 2nd
   harmonic produces the same dip, and within one frame the two look
   identical. only context separates them: the best lag just doubled
   against recent history, f0 was falling, level or CPP is low. subharmonic
   evidence is logged but not used in classification until step 6 designs
   a rule and validates it against the hand-labeled creak subset.

   hysteresis: leaving Voiced or Creak requires 2 consecutive frames of
   contrary evidence, so single-frame flicker doesn't strobe the display.
   during a hold (contrary evidence while the state stays Voiced), F0Hz
   publishes that frame's own candidate with its lowered confidence, and
   the display track keeps its last value instead of updating.

4. PUBLISH. F0Hz = NaN unless Voiced. Creak frames report Creak honestly —
   never a bogus 55 Hz. that's a lie the user will catch, and it destroys
   trust in every other number on screen.
```

output: `Silence | Unvoiced | Voiced | Creak` plus a 0..1 voicing confidence.

all thresholds above are config values with these defaults, and the whole classifier is scored against hand labels (§6). expect to retune.

note on creak coverage: creak periods are often longer than the 60 Hz search floor can represent, so the detector deliberately does not require a valid f0. it keys on aperiodicity, f0-candidate instability, and temporal evidence. the pulse-based detector (phase 1) will do better; this one just has to be honest.

descending glides and the bottom of range generate a lot of creak in practice sessions — for transfem users especially, but the adversarial corpus tasks make it everyone's code path. treat creak as high-traffic, and measure how often it occurs per task in the corpus rather than assuming.

### 3.4 f0 — YIN-FFT

- buffer **2048 samples** (42.7 ms @ 48 kHz), timestamped at center
- search range **60–1000 Hz** → lag τ from 48 to 800 samples. (v2.3: v2 capped the range at 600 Hz, but warm-up sirens go above that for many voices and the trace would drop out at the top. the safe-range gate should limit what charts ask for, not the tracker. a higher ceiling adds octave-up candidates for every voice, so the sweep suite adds octave-up bait (below). if the corpus shows more octave-up errors at the higher ceiling, fall back to per-exercise ceilings: 600 Hz for speech charts, 1000 Hz for sirens. configs are immutable, so the game builds one analyzer per exercise type.)
- **exact difference function, fixed integration window, centered on the buffer:**

  ```
  buffer B = 2048, max lag τmax = 800, evaluated to τmax + 24 = 824 (v2.4)
  integration length W = B − 824 = 1224
  (25.5 ms — longer than the longest period searched, 16.7 ms at 60 Hz)

  h(τ) = B/2 − ⌊(W + τ)/2⌋                     (start of the compared pair)
  d(τ) = Σ_{j=0}^{W−1} (x[h+j] − x[h+j+τ])²     for τ = 1..824
  ```

  the two compared windows together span `[h, h + τ + W)`, whose center sits within half a sample of the buffer center for every τ. at the last lag, h = 0 and the pair fills the buffer exactly. the 24 lags past τmax exist so the floor can be judged on a refined minimum, not on the edge of the array (below).

  **why a fixed window (v2.3):** v2 summed over `2048 − τ` terms, so d(τ) shrank at longer lags just because it had fewer terms. the YIN paper (de Cheveigné & Kawahara 2002) uses a fixed integration window to avoid exactly this. the shrinking sum favors long lags, which means octave-down errors. that bias only matters where 2τ fits inside the search range (f0 above ~120 Hz), so it lands on higher voices. it bites hardest when no dip clears the 0.15 threshold (breathy or noisy frames) and the global-minimum fallback decides.

  **why centered (v2.3, Astra review):** the first v2.3 draft compared a fixed head `x[0..W)` against `x[τ..τ+W)`. that pair is centered at `(W + τ)/2`, not at the buffer center, so the moment being measured moved with the detected period. on a 2400 cents/s glide, a frame timestamped at 300 Hz read 297.31 Hz (−15.6 cents). the error grows with glide rate and period, it leaks into reference comparisons and scoring, and it breaks the fixed-`AlgorithmicDelaySamples` claim in §1.1. the centered placement above measures 300.09 Hz (+0.5 cents) on the same glide.

  **compute it directly.** the centered placement means each lag uses a different window, so the single-FFT cross-correlation shortcut no longer applies. the direct sum is W·τmax ≈ 1 M multiply-adds per hop (~100 M/s). with `System.Numerics.Vector<float>` (8 lanes on AVX2) that's a few percent of one core. accumulate in float32 SIMD lanes and reduce in float64. **measure it at step 4** against the Gate B capture-to-result budget. if it doesn't fit (Steam Deck included), the fallback is the one-sided FFT form plus an explicit per-frame timestamp correction of `(W + τ)/2 − B/2` samples, and the glide tests below then gate that correction. a scalar float64 version of the same formula stays in `VoiceCore.Tests` as the **test oracle**, checked against the SIMD d(τ) on random and synthetic buffers.

- cumulative mean normalized difference `d′(τ) = d(τ)·τ / Σ_{j=1..τ} d(j)`, `d′(0) = 1`
- absolute threshold 0.15 — take the **first** dip below it, searching from **τ = 2**, not from τmin (see "out-of-range fundamentals" below); if none, take the global minimum within `[τmin, τmax]` and mark low confidence
- parabolic interpolation over the three points around the minimum for sub-sample lag
- output `f0RawHz` (always, per §3.3), and `f0Cents = 1200 · log2(f0 / 55.0)` — fixed 55 Hz anchor so the number is stable across sessions

**lag choice as built (v2.4, build step 4).** the plain rules above failed the §3.4 gates on breathy, noisy and floor-edge signals. each change below keeps the spec's behavior on clean signals, where the absolute threshold still decides:

- **dips are valleys, found on a lightly smoothed d′.** a "dip" is a contiguous run below the threshold, and its lowest point wins. the location is found on d′ smoothed by a moving average over ±1.5% of the lag (about ±26 cents), then refined to the raw minimum within that span, then parabolically interpolated on raw d′. in breathy frames the valley bottom is broad and flat (e.g. d′ 0.25 ± 0.01 across ±3% of the lag at HNR 5 dB), and picking a single raw sample wandered 100+ cents from noise alone. that broke STABLE and turned breathy frames Unvoiced. a clean valley is symmetric, so smoothing doesn't move it.
- **octave guard.** after the reference minimum is found (the first valley below 0.15, or the global minimum when there's none), the answer is the *first* valley that gets within 0.1 of the reference's depth. d′ at 2τ or 3τ is often slightly *lower* than at τ, because the cumulative mean it's normalized by keeps growing. breathy frames therefore picked 2τ or 3τ about half the time: the plain global-minimum fallback, and even the first-dip rule when τ's valley sat at 0.16 and 2τ's at 0.14. the guard stops that. the true period comes before its multiples, and a strong-H2 half-period dip is never that deep.
- **floor margin.** `F0Range = Below` when the refined minimum is more than 25 cents past τmax (under ~59.1 Hz), or the valley runs off the end of the evaluated lags. v2.3 said "the fallback lands at τmax". but a tone exactly at 60 Hz has its minimum exactly at τmax, and with noise the one-sample decision flipped half its frames to Below. the margin is larger than the estimator's jitter.

**out-of-range fundamentals (v2.3, Astra review).** a search that starts at τmin can't see a fundamental above the ceiling, but it can see that fundamental's multiples. a clean 1100 Hz sine has a period of 43.6 samples, just under τmin = 48, but two periods (87.3 samples) land inside the range with d′ ≈ 0. that frame then published as **550.03 Hz Voiced** with near-zero aperiodicity: a confident, folded wrong answer. so d(τ) is computed from τ = 2 (46 extra lags, ~6% more work), and the first-dip search runs from there:

- first qualifying dip at τ < τmin → the fundamental is above the ceiling. the frame is still classified on its aperiodicity as usual (§3.3), but `F0Hz` = NaN and `F0Range = Above`. no in-range multiple is ever accepted in its place. `F0RawHz` logs the true out-of-range estimate.
- τ = 2 corresponds to 24 kHz, so any tone below Nyquist that could fold into the range is caught. a 300 Hz harmonic complex with a strong 2nd harmonic still reads 300 Hz, because a voice has no periodicity at lags shorter than its period.
- below the floor (period > τmax) there is no in-range multiple to fold onto. a sub-60 Hz fundamental shows up as a missing or unstable candidate, which is the creak path (§3.3), or as `F0Range = Below` when its minimum lands clearly past τmax (the floor margin above).
- **STABLE** (§3.3) and the octave-correction median (§3.5) ignore out-of-range frames, and a hysteresis hold never publishes a folded value.
- the game treats confident-voiced `F0Range = Above` as a pitch Miss, not an abstention (game doc appendix A). the tracker saw the voice; it's just far past any chart target, since charts are authored ≥ 100 cents inside the search range. `Below` is a weaker signal, and the game abstains on it.

**validation (replaces v1's single sine test):**

- smoke test: synthesized 200 Hz sine → within ±2 cents. (demanding exactly 200.00 tests float trivia, not the algorithm.)
- sweep suite: sines and harmonic complexes at 60–1000 Hz in ~10% steps × several phases × amplitudes from −40 to −3 dBFS × with/without additive noise at 20 dB SNR. gate: ±5 cents on clean tones, and no octave errors on harmonic complexes with strong 2nd harmonics or a weak fundamental (octave-up bait, which matters more now the ceiling is 1000 Hz).
- breathy suite (v2.3): harmonic complexes at 150–400 Hz with steep spectral tilt plus aspiration-like noise, harmonics-to-noise ratio from 10 dB down to 0 dB. gate at HNR ≥ 5 dB: classified Voiced (via the breathy path, §3.3) once past onset, with no octave-down errors. 0–5 dB is reported but not gated, since that's where breathy phonation shades into whisper. this is the combined test for the breathy path and the fixed-window difference function.
- endpoint tests at exactly 60 and 1000 Hz, and just outside the range. 55 Hz → `F0Range = Below`, low confidence, or not Voiced. 1100, 1500 and 3000 Hz sustained sines and harmonic complexes → `F0Range = Above` with `F0Hz` = NaN. none of them may publish a folded value (e.g. 550 Hz for an 1100 Hz tone).
- ceiling-crossing glides (v2.3): sines and harmonic complexes gliding 800 → 1400 → 800 Hz at 1200 and 2400 cents/s. every frame is either within ±5 cents of the true f0 (in range) or `F0Range = Above` (out of range). a frame that publishes a fold is a failure.
- **timestamp alignment (v2.3):** linear-in-cents glides at ±600, ±1200 and ±2400 cents/s, across 100–900 Hz. the published `F0Hz` must match the synthesized instantaneous f0 at `WindowCenterSample` within ±5 cents at every rate, with no trend in error vs period. this test catches any off-center window. it also re-checks `AlgorithmicDelaySamples` from outside, by cross-correlating the published track against the known contour.

  **refined in v2.4 (build step 4).**
  - **mean error gate:** every timestamp case must also have mean error within ±1 cent. that's the check that actually catches an off-center window, since that shows up as systematic bias.
  - **pulse-timing margin (provisional):** for harmonic complexes (not sines), the per-frame tolerance adds the glide's change over half a period. that's the **measured behavior of this estimator**, not a limit on f0 estimation. on pulse-like signals during fast glides, the moment each YIN estimate describes wanders by up to about half a period, set by where the pulses fall in the window. at 115 Hz on a 2400 cents/s glide, that's frame-to-frame scatter of RMS 2.3 and max 9.1 cents, with zero mean and zero slope. other methods estimate instantaneous f0 through glissandi more tightly, so if the estimator changes (phase 1's pulse-synchronous analysis, or a nonstationary method), revisit this margin rather than inherit it. sines have no margin and meet ±5 at every rate. the mean-error and slope gates apply to every case, with or without the margin.
  - **corners excluded:** frames whose window straddles a contour corner (hold → glide) aren't cents-gated, because the "true f0 at the center" isn't what any windowed estimator measures there. they still count for voicing and gross errors.

### 3.5 octave error correction

this is where naive implementations quietly fail. budget real time here.

```
1. keep a running median of the last 5 valid f0Cents values
2. if |cents[n] − runningMedian| is between 1100 and 1300:
     - evaluate d′(τ/2) and d′(τ·2), skipping any lag outside the
       search range (τ·2 > 800 whenever f0 < 120 Hz). if τ/2 < τmin
       wins, the frame becomes F0Range = Above (§3.4), never a fold
     - if the alternate candidate's d′ is within 0.05 of the chosen one,
       prefer the candidate closer to the running median
3. median filter, width 5:
     - CAUSAL median (last 5 frames) for the display track. no formal
       lookahead, but a step change takes ~2–3 frames (20–30 ms) to pass
       through — count this as perceived lag in §5, because it is.
     - CENTERED median (±2 frames) exists ONLY in batch scoring output,
       reported in a separate column, never fed back into live claims.
4. slew limit 1200 cents/s — DISPLAY ONLY. the raw value is always logged.
5. never smooth across a voicing gap. reset the filter state.
```

frames therefore carry both `F0Hz` (published raw, post-octave-correction) and `F0DisplayHz` (median + slew). scoring uses `F0Hz`; the game renders `F0DisplayHz`.

### 3.6 formants — LPC

**voiced frames only.** per-formant validity: any formant that fails the checks below is NaN individually — a frame can have a valid F1/F2 and NaN F3/F4.

- **`maxFormantHz` drives the whole pipeline** (v1 said "configurable" but hard-coded 16 kHz/7.5 kHz — that contradiction is resolved as follows, Praat-style):
  - analysis rate `fs_a = 2 · maxFormantHz` (default `maxFormantHz` = 5500 → `fs_a` = 11 kHz; range 4000–8000)
  - resample 48 kHz → `fs_a`: Kaiser-windowed FIR lowpass, passband edge `0.42·fs_a`, stopband edge `0.5·fs_a`, stopband attenuation ≥ 70 dB. the FIR's group delay (linear-phase: (N−1)/2 samples) is **compensated in `WindowCenterSample`** so formant frames align with f0 frames.
  - accepted roots: `90 Hz < F < 0.95 · maxFormantHz`
  - LPC order = `round(2 + fs_a/1000)` (11 kHz → 13)
- pre-emphasis `y[n] = x[n] − 0.97·x[n−1]` at `fs_a`
- window 25 ms Hamming, hop 10 ms (same grid as everything else)
- autocorrelation → Levinson-Durbin → LPC coefficients
- root-find with the preallocated solver (§1.2)
- convert roots: `F = (fs_a / 2π)·|angle(z)|`, `BW = −(fs_a / π)·ln|z|`
- keep roots with F in the accepted range, `BW < 400 Hz`, positive imaginary part; sort ascending → candidate F1..F4

**tracking (expanded in v2 — nearest-neighbour alone is not enough):**

- assignment: greedy nearest-neighbour to previous frame's live tracks with cost `|ΔF| + 0.5·|ΔBW|`, subject to a max jump of **250 Hz per 10 ms hop for F1, 400 Hz for F2–F4**. candidates exceeding the jump limit start a *new* provisional track rather than corrupting an existing one.
- track death: a track unmatched for 3 consecutive frames dies; its slot reports NaN.
- track birth: a provisional track must survive 3 frames before it's published — this is what stops a spurious pole from wearing the "F2" label for half a second.
- per-formant confidence from bandwidth and track age; low-confidence formants are published with the confidence attached, so the game can choose its own floor.

**high f0 degrades LPC formants (v2.3).** as f0 rises, harmonics sample the spectral envelope more sparsely, and LPC poles tend to lock onto individual harmonics instead of the resonances between them. this is the direction transfem users move as they progress, so resonance measurement gets *less* reliable as users approach their goals. formant error is therefore reported per f0 band (§6), not only per device. per-formant confidence should drop with f0 once the corpus shows how fast accuracy falls off.

### 3.7 resonance estimate

- **formant dispersion**: `Df = mean(F(i+1) − F(i))` for i = 1..3, session-level, weighted toward F3/F4 (F1/F2 are vowel-dominated).
- **apparent VTL estimate**: `VTL ≈ c / (2·Df)`, `c = 35000 cm/s`. **experimental, batch-only in phase 0.** computed as an aggregate over *fixed-vowel sustained tasks only* — never per-frame on free speech, never shown live, always labeled "apparent." it is an acoustic length estimate under strong assumptions, not an anatomy readout and not a gender readout (§0).

any F1/F2-based comparison is only meaningful **within a fixed vowel** — tag every corpus file and every future exercise with its expected vowel so batch analysis groups correctly.

### 3.7b gameplay brightness proxy (experimental, new in v2.2)

the game's resonance lane may end up driven by something more robust than live LPC formants (game doc §1.5). phase 0 therefore implements at least one cheap spectral proxy alongside them: spectral centroid over 0–4 kHz and/or a low/mid band-energy ratio (e.g. 0–1 kHz vs 1–4 kHz), computed per hop on voiced frames and published as an experimental frame field (`BrightnessProxy`). it is evaluated on the resonance-manipulation corpus task (§6), against formant measurements, for **direction accuracy**, **test–retest stability**, **cross-device sensitivity**, and **specificity** (below). no user-facing meaning in phase 0; the formants-vs-proxy decision is made at the game doc's Gate B.

**the raw proxy is confounded with spectral tilt (v2.3).** centroid and band ratios rise whenever the spectral tilt flattens, and tilt flattens with vocal effort and with heavier, more pressed phonation. a raw brightness score therefore rewards pushing harder. that makes it a strain incentive in a product whose safety section forbids one (game doc §1.9). it also runs backwards for a common goal: lighter phonation steepens the tilt and reads *darker*. the proxy also rises with f0. so:

- **specificity is a gate criterion.** the corpus gains cross-talk tasks (§6): pitch, loudness and vocal weight each varied while tract posture is held fixed. the proxy must move more for a posture contrast than for any of those. report the ratio.
- **a tilt-normalized variant is scored alongside the raw one.** the variant is an `AnalysisConfig` choice (configs are immutable), so batch runs the corpus once per variant and the better one survives.
- **the fitted slope is published as `SpectralTiltDbPerKhz`** (experimental). it costs nothing once the normalization fit exists, and it is a starting input for the game's unscored weight meter (game doc §1.4).

**computation (v2.3, Astra review).** the first v2.3 draft computed the centroid directly on the detrended dB residual. a least-squares residual is signed and sums to zero over the fitted bins, so the centroid's denominator is ~0 and the result is undefined or unstable. band sums of dB residuals aren't energies either. residuals go back to linear power before anything is summed:

```
spectrum    reuse the CPP power spectrum (§3.8): 40 ms Hamming on the 48 kHz
            signal, same 10 ms grid. bin k at frequency f_k.
floor       P_k ← max(P_k, 1e−12)             (−120 dB, same floor as CPP)
analysis    bins with 100 Hz ≤ f_k < 4000 Hz, for BOTH variants
band        low  = 100 Hz–1 kHz,   high = 1–4 kHz

raw:        Q_k = P_k
normalized: L_k = 10·log10(P_k)
            fit L_k ≈ a + b·f_k by least squares over the analysis bins
            r_k = L_k − (a + b·f_k)          (signed residual, dB)
            Q_k = 10^(r_k / 10)              (positive linear power, flattened)
            SpectralTiltDbPerKhz = 1000·b

centroid    = Σ f_k·Q_k / Σ Q_k               over analysis bins, in Hz
band ratio  = 10·log10(Σ_high Q_k / Σ_low Q_k), in dB
```

`BrightnessProxy` publishes the centroid or the band ratio, whichever the config selects, so the raw/normalized × centroid/ratio grid is four configs. the proxy is NaN unless the frame is Voiced and not clipping. whether a published value is reliable enough to score is a separate question, covered by the resonance rules in §3.9. the fit weights every bin equally, including the harmonic valleys. at high f0 the valleys sink toward the floor and can tilt the fit. if the corpus shows `SpectralTiltDbPerKhz` tracking f0 at fixed posture, fit to per-harmonic peaks (located from `F0Hz`) instead.

### 3.8 voice quality (rescoped in v2)

- **CPP (dB)** — *kept in phase 0*, fully specified:
  - 40 ms Hamming window on the 48 kHz signal, same 10 ms grid
  - power spectrum in dB (10·log10, floor at −120 dB) → real cepstrum via inverse FFT of the log-power spectrum
  - peak search in quefrency `1/600 s .. 1/60 s` (v1's 60–500 Hz range conflicted with the f0 contract). v2.3 raised the f0 ceiling to 1000 Hz, but CPP stays capped at 600 Hz, because a peak near 1 ms would fall inside the low-quefrency region the regression excludes. frames with f0 > 600 Hz publish `CppDb = NaN`.
  - linear regression of cepstrum magnitude vs quefrency over `1 ms .. 16.7 ms`, excluding the first 1 ms (low-quefrency spectral-envelope region)
  - CPP = cepstral peak (dB) − regression value at the peak quefrency
  - diagnostic + creak/breathiness evidence only in phase 0; not gameplay-facing until validated against the corpus. it earns its place because it's cheap, needs no pulse tracking, and is the best single periodicity/breathiness measure available at this cost.
- **H1–H2**: **deferred to phase 1.** uncorrected H1–H2 is confounded by F1 proximity, window leakage, and mic response; the ±10% harmonic search also collides with neighboring harmonics at higher f0. when it returns, it returns with Iseli–Alwan formant correction and stays diagnostic.
- **jitter / shimmer: removed from phase 0.** differences between overlapping YIN frame estimates measure estimator movement, vibrato, and glides — not cycle-to-cycle perturbation. real jitter/shimmer need pulse-synchronous period and amplitude extraction (phase 1, alongside the pulse-based creak detector). nothing in phase 0 may consume these values, which is why the creak rule in §3.3 no longer references them.

### 3.9 confidence: defined and calibrated (new in v2.3)

the game abstains based on confidence. frames it can't trust aren't scored, and a run with too many of them isn't graded (game doc §1.2, appendix A). v2 published `F0Confidence` and `VoicingConfidence` without defining either, and a threshold on an undefined number is a guess. so:

- **meaning.** published confidences are calibrated probabilities, and each one answers a different question:
  - `VoicingConfidence` = P(the published voicing state matches the reference label). defined for **every** frame and every state. calibrated **per published state**, since Creak, Unvoiced and Voiced have very different base rates and error patterns. this is the confidence that lets the game call a confidently detected Creak or Unvoiced frame a Miss rather than an abstention.
  - `F0Confidence` = P(`F0Hz` is not a gross error, i.e. within 20% of reference | frame published Voiced). defined **only for Voiced frames** with `F0Range = In`. NaN otherwise. it says nothing about Creak or Unvoiced frames.
  - resonance: see below. **pitch confidence is not resonance confidence.** a frame can have a certain f0 and a missing or wrong F2.

  a game floor of 0.9 on `F0Confidence` then means "expect ≤ 10% gross pitch errors among the Voiced frames that get pitch-scored."
- **raw score.** before calibration, each confidence is a raw score built from evidence the analyzer already has. for f0: 1 − d′ at the chosen lag, candidate stability (§3.3), level above the noise floor, and whether octave correction stepped in. for voicing: distance from the nearest classification threshold, plus hysteresis state.

  as built (v2.4), both are monotone in each input, which is all a calibration map needs:
  - `F0Confidence` = periodicity × stability × level × dip × hold.
    - periodicity = `1 − d′/0.45`, clamped to 0..1
    - stability = 1 if STABLE, else 0.7
    - level = 0.5–1 over 0–20 dB above the level gate
    - dip = 1 if a dip cleared 0.15, else 0.5
    - hold = 0.5 during a hysteresis hold, else 1
  - `VoicingConfidence` = 0.5 + 0.5 × the margin to the threshold that would change the evidence, normalized to that band and clamped to 0..1. it's 0.5 during a hold.
- **calibration.** fit a monotone map from raw score to probability on the **dev split** (isotonic regression or binned), then check it on held-out (§6). the calibration table is part of `AnalysisConfig`, so the config hash covers it. recalibrating bumps `AnalyzerVersion`, so the game's PB provenance rule (game doc appendix A) archives old PBs instead of comparing across calibrations.
- **before step 5** there's no corpus to calibrate against. the raw score is published, and the config records `ConfidenceCalibration = none` so nothing downstream mistakes it for a probability.
- **as built (v2.5, build step 5).** `AnalysisConfig.Calibration` holds one monotone piecewise-linear map for `F0Confidence` and one per published voicing state. They're fit by binned isotonic regression: pre-bins of ≥ 50 frames that never split tied scores, pooled adjacent violators, then a Laplace-smoothed rate per block so no knot claims exactly 0 or 1. The table is generated source (`VoiceCore/FittedCalibration.cs`), so a refit is a reviewable diff. A state with no reference that can judge it keeps its raw score: today that's **Creak**, since a binary voiced/unvoiced reference can't say whether a Creak frame was right. `ConfidenceCalibrationTable.IsCalibrated(state)` tells the game, which should treat an uncalibrated confidence as below any floor. Fitting is dev only; held-out is scored untouched.

**resonance validity and confidence (v2.3, Astra review).** resonance gets its own rules, independent of `F0Confidence`:

- **validity** (a hard gate, before any confidence): the frame is Voiced, not clipping, and the signal the resonance lane uses is non-NaN. for formants, that's the specific formant(s) the lane reads (e.g. F2, or F1–F4 for dispersion), each individually valid per §3.6. for the proxy, it's `BrightnessProxy`.
- **confidence:** `FormantConfidence` = P(each formant the lane uses is within the formant-error tolerance of reference | valid), calibrated the same way once formant references exist (step 7). the Gate B decision (game doc §1.5) must ship the chosen signal with a confidence defined and calibrated like this. for the proxy, the reference is the formant measurement it's validated against. until one is calibrated, a resonance frame counts as reliable only if it passes validity, its level clears the §3.3 gate by ≥ 6 dB (a starting guess), and `VoicingConfidence` clears the floor. the config's `ConfidenceCalibration = none` tells the game this is a rule, not a probability.

---

## 4. output frame

```csharp
public enum VoicingState : byte { Silence, Unvoiced, Voiced, Creak }
public enum F0Range      : byte { In, Above, Below }

public readonly record struct AnalysisFrame
{
    // timing (§3.1)
    public long   WindowCenterSample   { get; init; }
    public long   ResultAvailableSample{ get; init; }
    public double TimeSeconds          { get; init; }  // WindowCenterSample / 48000.0

    // voicing
    public VoicingState Voicing        { get; init; }
    public float  VoicingConfidence    { get; init; }  // calibrated probability, §3.9

    // pitch
    public float  F0RawHz              { get; init; }  // internal candidate, always logged
    public float  F0Hz                 { get; init; }  // published; NaN unless Voiced
    public float  F0DisplayHz          { get; init; }  // causal median + slew; NaN unless Voiced
    public float  F0Cents              { get; init; }  // re 55 Hz, from F0Hz
    public float  F0Confidence         { get; init; }  // calibrated probability, §3.9; NaN unless Voiced and F0Range == In
    public F0Range F0Range             { get; init; }  // In | Above | Below, §3.4
    public float  Aperiodicity         { get; init; }  // YIN d′ at chosen lag

    // level
    public float  RmsDbfs              { get; init; }
    public float  PeakDbfs             { get; init; }
    public bool   Clipping             { get; init; }

    // formants — individually NaN when invalid (§3.6)
    public float  F1Hz { get; init; }  public float B1Hz { get; init; }
    public float  F2Hz { get; init; }  public float B2Hz { get; init; }
    public float  F3Hz { get; init; }  public float B3Hz { get; init; }
    public float  F4Hz { get; init; }  public float B4Hz { get; init; }
    public float  FormantConfidence    { get; init; }

    // voice quality
    public float  CppDb                { get; init; }

    // experimental (§3.7b) — NaN unless Voiced
    public float  BrightnessProxy      { get; init; }  // variant set by AnalysisConfig
    public float  SpectralTiltDbPerKhz { get; init; }
}
```

changes from v1: `readonly record struct` (v1's `readonly struct` with mutable public fields is a compile error, CS8340); two timestamps instead of one ambiguous `SampleIndex`; raw/published/display f0 all present; per-feature confidences instead of one global; jitter, shimmer, H1–H2, and per-frame dispersion/VTL removed (dispersion/VTL are session aggregates in batch output, §3.7).

use `NaN` for "not measured", never `0`. a zero will silently poison every average computed downstream.

analyzer version + config hash go in the Parquet file metadata and session log header, once, not in every frame.

---

## 5. latency budget

using the §3.1 definitions. algorithmic delay (event → frame that reflects it):

| stage | cost |
|---|---|
| device/driver input buffering | 5–20 ms (measure per device; not free) |
| capture buffer (256 samples) | 5.3 ms |
| YIN window half-width | 21.3 ms |
| analysis compute + scheduling | ~1–3 ms (measure; overruns are bugs) |
| causal median settling on transients | ~20–30 ms perceived on steps (§3.5) |
| render queue + 60 fps scanout | 16–33 ms |
| **user-to-photon, realistic** | **~70–110 ms on steps, ~50–80 ms steady-state** |

capture-to-result (§3.1) is only the "analysis compute + scheduling" row. it's the part optimization can actually move, which is why it gets its own threshold at Gate B. the YIN half-width row is algorithmic delay, a constant.

v1's "~43 ms" counted only the parts that are easy to count. the honest number is worse, and it still cannot be fixed by optimization — YIN needs 2–3 periods, and at 80 Hz that's 25–37 ms of audio before any answer *exists*.

this is why the game is a **tracing** game, not a **hitting** game. sustained lines and glides tolerate this; tight timing windows do not. **measure user-to-photon with the camera test (§3.1) on real hardware before committing to any chart design** — the loopback test only measures the audio half, and gates in §6 are on measured numbers, not this table.

---

## 6. batch mode and the test harness

this is the part that makes the difference between "seems fine on my voice" and "excellent."

### input

```
corpus/
  manifest.csv       speaker_id, file, source, session_id, device, os,
                     condition, task, vowel, target_f0,
                     os_processing_flags (AGC/NS on|off|unknown), consent_ref
                     (source: actor | self | volunteer | public — see
                     "recording sources"; session_id links files captured
                     at the same time on different devices)
  audio/*.wav        48 kHz mono (44.1 kHz accepted; resample logged)
```

### output

per-file Parquet on the frame schema, plus a run summary CSV, plus (offline only) the centered-median scoring column (§3.5).

### reference — layered, not "ground truth"

Praat agreement can just mean two systems share the same assumptions, especially for formants and creak. the reference stack:

1. **synthetic signals** with mathematically known f0 (and known formants via Klatt-style synthesis when we get to formant gates) — the only true ground truth we have.
2. **Praat via parselmouth** as a *versioned comparison baseline*: pin the Praat version and record every setting (pitch floor/ceiling, time step, max formant, LPC order) in the run summary. same 10 ms grid.
3. **hand-labeled hard subset**: creak, soft phonation, onsets/offsets, octave-error bait. labeled per a written annotation guide; creak labels from 2 annotators with inter-rater agreement reported. disagreement frames are excluded from gates and reported separately.
4. **EGG / laryngograph references: partly available now** (v2.3; v2 said "later, if available"). public corpora recorded with a laryngograph give true f0 ground truth on real speech: PTDB-TUG and the Keele pitch database. Hillenbrand et al.'s vowel set (men, women and children) gives hand-checked formant values, including high-f0 cases. these are studio-condition and not this product's population, so they supplement the corpus rather than replace it. **check each license** before using it in a commercial product's development. if a paid session can be run with an EGG (e.g. through a university voice lab), those recordings get the same ground truth.

**real recordings are the product gate; synthetic signals are unit-test infrastructure.** synthetic signals are the only *exact* ground truth, so they own the algorithm tests (§3.4). but no threshold is accepted on synthetic data alone. **the causal live output on real recordings is the product gate.** centered-offline numbers are reported separately and never substituted.

### metrics

| metric | definition | notes |
|---|---|---|
| GPE | % of ref-voiced frames where \|f0 − f0_ref\| / f0_ref > 0.20 | ref-creak frames excluded, reported separately |
| FPE | RMSE in cents on frames voiced in both and not GPE | |
| VDE | % frames with mismatched voiced/unvoiced decision | plus full 4-state confusion matrix (Silence/Unvoiced/Voiced/Creak) |
| voicing P/R | precision & recall for Voiced, and for Creak vs hand labels | |
| formant error | mean \|F − F_ref\| per formant (F1–F4 each), voiced frames | F3/F4 gated too — resonance estimates depend on them. reported per f0 band (§3.6) |
| confidence calibration | per confidence bin: predicted vs observed non-GPE rate; expected calibration error | v2.3, §3.9. scored on held-out |
| coverage at floor | % of ref-voiced frames published Voiced with both VoicingConfidence and F0Confidence ≥ floor, at floors 0.5 / 0.8 / 0.9. resonance coverage is reported separately with the resonance validity rules (§3.9) | v2.3. the number of frames the game will actually be allowed to score. an analyzer can post a good GPE by marking hard frames low-confidence; this metric catches it |
| out-of-range folds | % of frames with ref f0 outside the search range that publish an in-range F0Hz | v2.3, §3.4. target 0 on synthetic, reported on real |
| resonance specificity | proxy/formant change for a posture contrast ÷ change for pitch, loudness, and weight contrasts (each separately) | v2.3, §3.7b. cross-talk tasks |
| p95 capture-to-result | measured (§3.1 definition) | includes the direct-sum YIN cost (§3.4) |
| user-to-photon | measured, camera test | reported, not gated in phase 0 |

**aspirational targets** (clean-slice): GPE < 2%, FPE < 15 cents, VDE < 5%, F1/F2 error < 60 Hz. but:

- **a target only gates against a reference that can resolve it** (v2.5). the first multi-speaker reference, PTDB-TUG's laryngograph RAPT track, scores Praat itself at FPE 25 cents RMS (median 8), so FPE < 15 is reported but not gated until a finer reference exists (synthetic truth still gates fine error in §3.4). docs/corpus.md records the measurement.

- **gates activate per slice only when the slice has ≥ 3 speakers and ≥ 10 files.** below that, metrics are reported with bootstrap 95% CIs and do not gate CI — worst-slice gating on a 2-file slice is a coin flip.
- the corpus is split **dev / held-out** (by speaker, not by file). thresholds are tuned on dev; the held-out set is scored untouched and reported alongside.
- until slices reach minimum size, CI gates are **regression gates**: no metric may worsen by more than its CI vs the last accepted run.

**compute everything per corpus slice, not just in aggregate.** a global pass rate hides failing entirely on gaming headsets. once slices are big enough, gate CI on the worst qualifying slice. slices cut by device/condition, by task, and (v2.3) by **f0 band**: < 150 Hz, 150–250 Hz, > 250 Hz. the octave-down bias (§3.4) and the LPC degradation (§3.6) both depend on f0, and an aggregate would average them away.

**as built (v2.5):** `VoiceCore.Batch corpus run` implements this section. docs/corpus.md lists the choices the table leaves open:

- GPE is computed over frames voiced in both, with a Voiced frame publishing no f0 on an in-range reference counted as gross.
- the manifest adds `noise_floor_dbfs` and `reference` columns.
- the reference file format is defined there.
- per-band VDE assigns unvoiced frames to the file's median reference f0.
- the run report adds a median |fine| error and a fine bias.

### corpus contents

- **conditions**: studio/condenser, laptop built-in, gaming headset, noisy room; with OS processing flags recorded (and disabled where possible)
- **tasks**:
  - sustained /a/ /i/ /u/ at 5 pitch targets
  - ascending and descending glides, including **sirens that go above 600 Hz** (§3.4)
  - **soft/breathy sustained phonation** at near-floor level. this tests the §3.3 level gate and breathy path.
  - **resonance manipulation**: same speaker, same vowel, same pitch target, contrasted tract postures (e.g. instructed "bright/forward" vs neutral), repeated across devices and across sessions. this task feeds the game's resonance-signal gate (game doc §1.5).
  - **resonance cross-talk** (v2.3): tract posture held fixed while one other thing changes. (a) a slow pitch glide, (b) soft / normal / loud at one pitch, (c) light vs heavy vocal weight at one pitch. these score specificity (§3.7b).
  - read passage (Rainbow Passage, public domain)
  - spontaneous speech
- **adversarial**: deliberate creak, deliberate falsetto, breathy onset, whisper, cough, laugh, background music. if the game plays reference tones (game doc part 4), add the target tone playing through laptop speakers, with and without the speaker voicing.
- **speakers**: as wide an f0 range as recruitable, with coverage across 80–350 Hz in speech and higher in sirens, multiple voice types, and not just your own voice. include trans speakers, including voices mid-transition. they're the product's actual population, and neither public corpora nor synthetic signals cover them.

**recording sources (new in v2.3)**, in order of how much each is worth per hour:

- **paid voice actors / voice coaches.** they're best at the *controlled* tasks: resonance manipulation, cross-talk, deliberate creak/breathy/falsetto, and sirens. they can produce a contrast on cue and repeat it across sessions. run each paid session with **several devices recording at once** (condenser, laptop mic, gaming headset, Steam Deck side by side). one performance then yields several device slices with identical content, which isolates the device effect cleanly. but one actor is one vocal tract: **they count as one speaker for slice minimums** no matter how many voices they perform. reaching ≥ 3 speakers means ≥ 3 people.
- **the developer's own practice sessions**, recorded through the probe. this is free, has high volume, and is the fastest way to reach the 20-file debug corpus. it's still a single speaker.
- **volunteers** from the community, later. same consent requirements, and the widest range of voices.
- **public reference corpora** (reference layer 4 above), for f0/formant ground truth and speaker breadth from day one.

every human recording, paid or not, needs the §0 consent terms in writing: commercial development use, derived measurements, retention period, and the right to be removed. for paid work, put them in the recording release/contract before the session, not after. store the reference as `consent_ref` in the manifest.

start with 20 files — that's a *debug* corpus and it will find bugs immediately. it is not enough to freeze acceptance thresholds; grow toward the slice minimums above before gates go hard.

---

## 7. calibration parameters

persisted, settable, exposed in the probe UI:

```
inputDevice
sampleRate            (48000; 44100 accepted → resampled)
inputGainDb
noiseFloorDbfs        (measured, §3.2)
voicedLevelMarginDb   (8, §3.3)
latencyOffsetMs       (phase 0 baseline via loopback; the game calibrates two
                      offsets per player instead: game doc §1.7)
f0SearchMinHz         (60)
f0SearchMaxHz         (1000; 600 fallback per §3.4)
maxFormantHz          (5500 default; drives fs_a, LPC order, root range — §3.6)
```

---

## 8. build order

do not reorder these. each one de-risks the next. (v1's order had YIN before the streaming skeleton and synthetic harness; that's backwards — the contract and the tests are what make every later stage cheap to validate.)

1. **streaming skeleton + timing model.** `VoiceAnalyzer` shell, ring buffer, frame queue, timestamps, `Reset`, diagnostics counters. chunk-boundary invariance test passes on a passthrough "analyzer."
2. **synthetic signal generator + test harness.** sines, harmonic complexes, glides, noise mixes, breathy signals (§3.4). this is infrastructure for every later gate.
3. **capture → RMS meter** in the Godot probe. confirms devices work and exercises the format policy. measure loopback latency now, and run the camera test once for a baseline. start the native-vs-`AudioEffectCapture` comparison here, since the probe can host both.
4. **YIN (causal) + voicing state machine**, with the float64 oracle test. validate against the synthetic sweep, breathy, out-of-range, and timestamp-alignment suites (§3.4), then against Praat on a few recorded files. measure the direct-sum cost on the slowest target (Steam Deck) and decide here whether the FFT-plus-correction fallback is needed. **then add a live scrolling log-frequency pitch trace to the probe** (v2.3). it's the first thing in the build a person would use, it makes every later bug visible by eye, and your own practice through it seeds the debug corpus. this is not step 8 early. step 8 tests *two* dimensions together, which needs step 7.
5. **batch mode + 20-file debug corpus + metrics script.** ← get here before formants and before octave-correction tuning. the metrics tell you whether anything works. the debug corpus can be your own sessions plus public references (§6). fit the first confidence calibration here (§3.9).
6. **octave correction + creak, tuned against the corpus.** grow the corpus (first paid session fits here, see §6 recording sources); add hand labels for the hard subset; design and validate the subharmonic rule (§3.3).
7. **formants + resonance + brightness proxy (§3.7b).** re-run metrics with F1–F4 gates; score formants and both proxy variants on the resonance-manipulation and cross-talk tasks.
8. **live visualization.** log-frequency pitch line plus a second resonance indicator — find out now whether two simultaneous tracked dimensions are legible or overwhelming.

**Gate A (analyzer viability) after step 6:** if GPE and VDE won't come down on the noisy, soft, and creaky slices, change approach while it's cheap. v2.3 adds **coverage on the soft/breathy slice** at the game's default confidence floor. if too few breathy frames clear the floor, typical breathy runs trip the game's 20% unreliable-run rule and come back ungraded, which fails a core practice mode even when GPE looks fine. **Gate B (product readiness) after step 8** is owned by the game design doc: resonance-signal decision (formants vs §3.7b proxy on the resonance-manipulation task), two-dimension legibility, and the product latency thresholds — this spec measures p95 capture-to-result and user-to-photon and reports them; the thresholds on those numbers are product requirements and live in the game doc. deferred to phase 1: pulse-synchronous analysis (real jitter/shimmer, better creak), H1–H2 with formant correction, any user-facing VTL.

---

## 9. change log vs v1

(v2.3: renumbered from §12, since §9–11 never existed.)

accepted from the Sol review: streaming stateful API with chunk invariance (§1.1); voicing/creak contradiction fixed via explicit decision order and multi-frame creak (§3.3); jitter/shimmer removed as invalidly defined (§3.8); frame struct compile fix + timestamps + per-feature confidence (§4); latency semantics split into algorithmic/capture-to-result/user-to-photon (§3.1, §5); Praat demoted to versioned baseline within a layered reference stack (§6); exact YIN difference formula + tolerance-based sweep tests (§3.4); ring overflow policy (§2); `maxFormantHz` made to actually drive the pipeline (§3.6); formant track birth/death rules (§3.6); VTL rescoped to experimental batch aggregate (§3.7); dev/holdout split, slice minimums, confusion matrix (§6); device format + OS-processing policy (§3, §6); analysis/interpretation boundary and privacy requirements promoted to §0.

departures from the Sol review: **CPP stays in phase 0** — unlike jitter/shimmer it needs no pulse extraction, its v1 spec gaps were fixable (§3.8), and it's the cheapest honest breathiness/periodicity evidence we have, which the creak work wants anyway; it just stays diagnostic. build order also front-loads the synthetic harness before capture (Sol had capture first).

v2.2 (2026-08-19, from the second Sol review of the game design doc): resonance-manipulation corpus task added to §6; experimental brightness proxy specified (§3.7b) with a `BrightnessProxy` frame field; build-order gate renamed Gate A, with Gate B (product readiness, after step 8) defined in the game design doc — latency thresholds clarified as product requirements measured here but gated there.

new in v2 beyond both: level gate lowered to +8 dB with soft-phonation corpus coverage (v1's +12 dB would classify quiet breathy practice — a core use case — as silence); left-channel downmix rationale; causal-median settling counted honestly as perceived lag; creak detection decoupled from a valid f0 so sub-60 Hz creak still classifies.

v2.3 (2026-09-28, fresh-eyes review before any code):

- **voicing**: breathy path (stable f0 candidate + elevated aperiodicity → Voiced, reduced confidence), because v2 would have filed breathy phonation as Creak or Unvoiced. v2's subharmonic test was non-discriminating and is now logged-only until step 6. ZCR defined. what gets published during a hysteresis hold is defined (§3.3).
- **YIN**: fixed-integration-window difference function per the YIN paper, because v2's shrinking sum biased toward octave-down errors on higher voices. search ceiling 1000 Hz for sirens, with a fallback to 600 Hz per exercise. float64 test oracle. breathy and octave-up test suites (§3.4). the octave-correction lag range is bounded (§3.5).
- **confidence**: defined as calibrated probabilities, fit on dev and checked on held-out, versioned with the config. the metrics add calibration and coverage-at-floor (§3.9, §6). Gate A adds breathy-slice coverage (§8).
- **plumbing**: frame queue alongside the triple buffer, so traces and scoring see every frame (§2). capture-to-result measured from the frame's last sample (§3.1, §5). `MaxFramesFor` output-capacity contract (§1.1).
- **resonance**: the brightness proxy's confound with spectral tilt/effort is spelled out as a strain incentive. specificity is a gate criterion with cross-talk corpus tasks. a tilt-normalized variant is added. `SpectralTiltDbPerKhz` is published (§3.7b). LPC degradation at high f0 is noted, and metrics are sliced by f0 band (§3.6, §6).
- **corpus**: real recordings are the product gate. a recording-sources section covers paid voice actors (one actor = one speaker; record multiple devices at once), own sessions, volunteers, and public laryngograph-referenced corpora with license checks. trans and mid-transition voices are named as coverage (§6).
- **project**: the probe moves from Avalonia to Godot. MathNet is replaced by a preallocated root finder (it allocated per call, breaking §1.1). the TFM note covers .NET 8 end of support (§1, §1.2). build order: live pitch trace at step 4, capture-path comparison at step 3 (§8).

v2.4 (2026-09-29, build step 4 findings — YIN and voicing implemented and gated against the synthetic suites):

- **YIN lag choice** (§3.4): valleys found on d′ smoothed over ±1.5% of the lag, refined on raw d′. an octave guard takes the first valley within 0.1 of the reference minimum. `Below` requires the refined minimum more than 25 cents past τmax, so d′ is evaluated to τmax + 24 and W = 1224. without these, breathy frames picked 2τ or 3τ about half the time, flat valleys jittered 100+ cents, and exact-60 Hz tones flipped to Below.
- **timestamp-alignment gate** (§3.4): a mean-error gate of ±1 cent (the real off-center check), a provisional pulse-timing margin for harmonic complexes on glides, and contour corners excluded from the cents gate. the margin reflects this YIN's measured per-frame scatter on pulse-like signals at low f0 (RMS 2.3, max 9.1 cents at 2400 cents/s). it isn't a claim that no method could do better. sines still meet ±5 everywhere. the mean and slope gates catch timing bias either way.
- **raw confidences defined** (§3.9).
- **measured cost:** 0.14 ms mean, 0.33 ms p99 per frame on a desktop (AVX2), ~1.5% of one core. the direct sum stands; the FFT fallback isn't needed there. `VoiceCore.Batch bench` repeats the measurement on a Steam Deck.

v2.5 (2026-10-01, build step 5: batch mode, debug corpus, metrics, first confidence calibration):

- **batch mode and metrics** (§6) as specified, with per-file Parquet, a run summary with bootstrap CIs by file, qualifying-slice target gates and regression gates against an accepted run. docs/corpus.md covers the corpus layout and every scoring choice.
- **first corpus:** 80 PTDB-TUG files (20 speakers, 4 sentences each, studio headset read speech) with laryngograph references. 14 speakers are dev and 6 held-out, so the held-out split is speaker-independent from the start. its frame timing isn't documented by the corpus. the offset (22 ms) was measured against Praat, not VoiceCore.
- **results, analyzer 0.3.0, causal output** (dev / held-out):
  - GPE 2.7% / 4.1%
  - VDE 6.3% / 5.7%
  - FPE 25 / 26 cents (median 8)
  - **f0 < 150 Hz is the worst slice:** GPE 4.5% / 7.1%, Voiced recall 76% / 73%. that's octave-down errors and missed low-energy voicing, which is step 6's target.
  - Praat (offline, path-smoothed) on the same reference: GPE 1.6% / 3.2%, VDE 4.9% / 3.7%, FPE 25 cents.
- **calibration** (§3.9): raw `F0Confidence` was strongly underconfident (raw 0.2–0.3 was right 97% of the time). on held-out speakers, calibration takes the F0Confidence ECE from 0.31 to 0.01 and coverage at a 0.8 floor from 39% to 68%. Creak stays uncalibrated until hand labels exist. analyzer version 0.2.0 → 0.3.0.
- **FPE target not gated** against a reference that can't resolve it (§6).
- not yet: the developer's own recordings and Praat references for them (tooling ready), 44.1 kHz resampling, the centered-offline column (§3.5), the breathy coverage gate.

v2.3 fixes from the Astra review (numerically checked with small probes; no implementation existed yet):

- **YIN window centering**: the first draft's fixed head window put the measured moment at `(W + τ)/2`, not the buffer center. that cost −15.6 cents on a 2400 cents/s glide at 300 Hz and made the algorithmic delay depend on pitch. the compared pair is now centered on the buffer for every lag. it's computed as a SIMD direct sum, since the single-FFT shortcut no longer applies, with an FFT-plus-timestamp-correction fallback if step 4 profiling says so. timestamp-alignment glide tests added (§3.4).
- **out-of-range fundamentals**: a search starting at τmin accepted in-range multiples of out-of-range periods. a clean 1100 Hz sine published as 550.03 Hz Voiced. the first-dip search now starts at τ = 2, and above-ceiling frames publish `F0Hz` = NaN with a new `F0Range` field. out-of-range and ceiling-crossing tests and an out-of-range-fold metric added (§3.4, §3.5, §4, §6).
- **tilt-normalized proxy**: the centroid was computed on signed dB residuals, which sum to zero. residuals now go back to linear power before any centroid or band sum. the spectrum, bands, and −120 dB floor are specified (§3.7b).
- **confidence per dimension**: `VoicingConfidence` is defined for every state and calibrated per state. `F0Confidence` is defined only for in-range Voiced frames. resonance gets separate validity and confidence rules. coverage-at-floor requires both voicing and f0 confidence (§3.9, §6).
