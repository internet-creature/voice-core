# voice analysis prototype — technical spec v2

phase 0 deliverable. the goal is not a game. the goal is to answer "is the tracking good enough to build on?" with numbers, before any engine work happens.

v2 incorporates the Sol review plus a second pass. changes from v1 are summarized at the end (§12).

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
VoiceCore/           net8.0 class library
  - zero UI, zero audio-device code, zero platform deps
  - stateful streaming analyzer: push samples in, frames come out
  - this is the assembly Godot will reference later, unchanged

VoiceCore.Batch/     console app
  - runs VoiceCore over a corpus of WAVs *through the same streaming path*
  - emits per-frame Parquet + summary CSV

VoiceProbe/          Avalonia app
  - live mic capture, minimal render, frame logging
  - disposable. do not put logic here.

VoiceCore.Tests/     xUnit
```

**VoiceCore must never know what a microphone is.** it takes buffers. the probe and the game both feed it.

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

    /// consumes input, writes any completed frames to output,
    /// returns the number written. zero-allocation in steady state.
    public int Process(ReadOnlySpan<float> input, Span<AnalysisFrame> output);

    public AnalyzerDiagnostics Diagnostics { get; }  // counters, see §2
}
```

**chunk-boundary invariance is a contract, and a test:** feeding the same signal in chunks of 1, 480, 4096, or random sizes must produce bit-identical frames. batch mode and Godot use this exact path — there is no separate offline code path for the causal analyzer. (offline *scoring* may additionally run centered filters; see §3.4.)

`AnalysisConfig` is immutable; changing parameters means constructing a new analyzer. the config carries an `AnalyzerVersion` string and a content hash, stamped into every Parquet file and log header (not into every frame).

### 1.2 dependencies

| package | project | license |
|---|---|---|
| PortAudioSharp2 (or NAudio if Windows-only) | VoiceProbe | MIT |
| FftSharp | VoiceCore | MIT |
| MathNet.Numerics (eigenvalues for LPC roots) | VoiceCore | MIT |
| Parquet.Net | VoiceCore.Batch | Apache-2.0 |
| Avalonia + SkiaSharp | VoiceProbe | MIT |

all permissive. caveat: FftSharp's public API allocates per call. either wrap it with pooled buffers behind an internal `IFftPlan` interface, or hand-roll the real FFT (~100 lines). the interface also keeps the door open to dropping MathNet (Bairstow's method) if we want VoiceCore dependency-free before shipping.

---

## 2. threading and buffering

```
capture callback (realtime thread)
    └─> lock-free SPSC ring buffer (2^17 floats ≈ 2.7 s @ 48 kHz)
            └─> analysis thread (dedicated, not thread pool)
                    └─> triple-buffered latest AnalysisFrame
                            └─> UI thread reads without locking
```

rules for the capture callback: no allocation, no locking, no logging. write and return.

ring buffer: power-of-two size, `Interlocked` read/write indices, mask instead of modulo.

**overflow policy (new in v2):** a 2.7 s buffer must never become 2.7 s of stale feedback. if the analysis thread falls behind:

- live mode **drops the oldest audio** to bring backlog under 100 ms,
- calls `Reset()` on the analyzer (temporal state is invalid across the gap),
- increments `Diagnostics.OverrunCount` and `Diagnostics.DroppedSamples`.

batch mode never drops; it just runs slower than realtime.

`AnalyzerDiagnostics`: overrun count, dropped samples, frames produced, max analysis time per frame. logged per session; overruns > 0 on target hardware is a bug.

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
- **capture-to-result latency** = wall-clock from a sample entering the ring buffer to its frame being readable by the UI thread. measured, includes scheduling and analysis time.
- **user-to-photon latency** = mouth to pixels. includes device/driver buffering, capture-to-result, render queuing, and display scanout. **only measurable end-to-end** — an audio loopback test does not capture the render half. measure with a clap-to-screen-flash camera test (240 fps phone camera is sufficient).

v1's latency table mixed these. the budget in §5 uses the definitions above.

### 3.2 preprocessing

- **DC removal**: one-pole high-pass, `y[n] = x[n] - x[n-1] + 0.995 * y[n-1]` (fc ≈ 38 Hz @ 48 kHz — below the 60 Hz search floor, fine)
- **no AGC on the analysis path.** if a monitoring path wants AGC, it's a separate branch.
- **RMS** over the hop → dBFS
- **peak** over the hop → dBFS, plus a clipping flag at > −0.1 dBFS
- **noise floor**: measured during an explicit calibration step (2 s of instructed silence → 10th percentile of hop RMS), persisted. during use, adapt slowly (time constant ~10 s) but **only during frames already classified Silence** — never let speech drag the floor up. v1 said both "rolling 5 s" and "captured during calibration"; this replaces both.

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

3. CLASSIFY on {aperiodicity, ZCR, level, temporal evidence}:
     aperiodicity < 0.20                      → Voiced
     0.20 ≤ aperiodicity < 0.45, low ZCR      → Creak candidate
     else                                      → Unvoiced
   CREAK IS A MULTI-FRAME CALL, not a single-frame threshold: a creak
   candidate is published as Creak only when ≥3 of the last 5 non-silent
   frames are creak candidates (subharmonic evidence — d′(2τ) competitive
   with d′(τ) — counts as a candidate too, since creak often presents as
   period doubling rather than raw aperiodicity). until then it is
   published as Unvoiced with the candidate logged.
   hysteresis: leaving Voiced or Creak requires 2 consecutive frames of
   contrary evidence, so single-frame flicker doesn't strobe the display.

4. PUBLISH. F0Hz = NaN unless Voiced. Creak frames report Creak honestly —
   never a bogus 55 Hz. that's a lie the user will catch, and it destroys
   trust in every other number on screen.
```

output: `Silence | Unvoiced | Voiced | Creak` plus a 0..1 voicing confidence.

all thresholds above are config values with these defaults, and the whole classifier is scored against hand labels (§6). expect to retune.

note on creak coverage: creak periods are often longer than the 60 Hz search floor can represent, so the detector deliberately does not require a valid f0 — it keys on aperiodicity, subharmonics, and temporal evidence. the pulse-based detector (phase 1) will do better; this one just has to be honest.

descending glides and the bottom of range generate a lot of creak in practice sessions — for transfem users especially, but the adversarial corpus tasks make it everyone's code path. treat creak as high-traffic, and measure how often it occurs per task in the corpus rather than assuming.

### 3.4 f0 — YIN-FFT

- window **2048 samples** (42.7 ms @ 48 kHz), timestamped at center
- search range **60–600 Hz** → lag τ from 80 to 800 samples
- **exact difference function** (this is where FFT shortcuts go wrong):

  ```
  d(τ) = Σ_{j=0}^{W−τ−1} (x[j] − x[j+τ])²
       = E_head(W−τ) + E_tail(τ) − 2·R(τ)

  E_head(m) = Σ_{j=0}^{m−1} x[j]²          (one cumulative-sum array
  E_tail(τ) = Σ_{j=τ}^{W−1} x[j]²           serves both)
  R(τ)      = Σ_{j=0}^{W−τ−1} x[j]·x[j+τ]
  ```

  R(τ) via FFT: zero-pad the 2048-sample window to **4096** (≥ 2W, prevents circular wraparound), forward FFT, multiply by conjugate, inverse FFT. the naive `2(R(0) − R(τ))` form is biased at large lags because window energy changes with lag — use the three-term form above.

- cumulative mean normalized difference `d′(τ) = d(τ)·τ / Σ_{j=1..τ} d(j)`, `d′(0) = 1`
- absolute threshold 0.15 — take the **first** local minimum below it; if none, take the global minimum and mark low confidence
- parabolic interpolation over the three points around the minimum for sub-sample lag
- output `f0RawHz` (always, per §3.3), and `f0Cents = 1200 · log2(f0 / 55.0)` — fixed 55 Hz anchor so the number is stable across sessions

do the FFT version, not naive YIN. naive is ~1.5 M ops per hop; the FFT version is roughly 50× cheaper.

**validation (replaces v1's single sine test):**

- smoke test: synthesized 200 Hz sine → within ±2 cents. (demanding exactly 200.00 tests float trivia, not the algorithm.)
- sweep suite: sines and harmonic complexes at 60–600 Hz in ~10% steps × several phases × amplitudes from −40 to −3 dBFS × with/without additive noise at 20 dB SNR. gate: ±5 cents on clean tones, no octave errors on harmonic complexes with strong 2nd harmonics.
- endpoint tests at exactly 60 and 600 Hz, and just outside the range (55, 650 Hz → low confidence or unvoiced, never a folded wrong answer).

### 3.5 octave error correction

this is where naive implementations quietly fail. budget real time here.

```
1. keep a running median of the last 5 valid f0Cents values
2. if |cents[n] − runningMedian| is between 1100 and 1300:
     - evaluate d′(τ/2) and d′(τ·2)
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
- root-find via companion-matrix eigenvalues
- convert roots: `F = (fs_a / 2π)·|angle(z)|`, `BW = −(fs_a / π)·ln|z|`
- keep roots with F in the accepted range, `BW < 400 Hz`, positive imaginary part; sort ascending → candidate F1..F4

**tracking (expanded in v2 — nearest-neighbour alone is not enough):**

- assignment: greedy nearest-neighbour to previous frame's live tracks with cost `|ΔF| + 0.5·|ΔBW|`, subject to a max jump of **250 Hz per 10 ms hop for F1, 400 Hz for F2–F4**. candidates exceeding the jump limit start a *new* provisional track rather than corrupting an existing one.
- track death: a track unmatched for 3 consecutive frames dies; its slot reports NaN.
- track birth: a provisional track must survive 3 frames before it's published — this is what stops a spurious pole from wearing the "F2" label for half a second.
- per-formant confidence from bandwidth and track age; low-confidence formants are published with the confidence attached, so the game can choose its own floor.

### 3.7 resonance estimate

- **formant dispersion**: `Df = mean(F(i+1) − F(i))` for i = 1..3, session-level, weighted toward F3/F4 (F1/F2 are vowel-dominated).
- **apparent VTL estimate**: `VTL ≈ c / (2·Df)`, `c = 35000 cm/s`. **experimental, batch-only in phase 0.** computed as an aggregate over *fixed-vowel sustained tasks only* — never per-frame on free speech, never shown live, always labeled "apparent." it is an acoustic length estimate under strong assumptions, not an anatomy readout and not a gender readout (§0).

any F1/F2-based comparison is only meaningful **within a fixed vowel** — tag every corpus file and every future exercise with its expected vowel so batch analysis groups correctly.

### 3.7b gameplay brightness proxy (experimental, new in v2.2)

the game's resonance lane may end up driven by something more robust than live LPC formants (game doc §1.5). phase 0 therefore implements at least one cheap spectral proxy alongside them: spectral centroid over 0–4 kHz and/or a low/mid band-energy ratio (e.g. 0–1 kHz vs 1–4 kHz), computed per hop on voiced frames and published as an experimental frame field (`BrightnessProxy`). evaluated on the resonance-manipulation corpus task (§6), against formant measurements, for **direction accuracy**, **test–retest stability**, and **cross-device sensitivity**. no user-facing meaning in phase 0; the formants-vs-proxy decision is made at the game doc's Gate B.

### 3.8 voice quality (rescoped in v2)

- **CPP (dB)** — *kept in phase 0*, fully specified:
  - 40 ms Hamming window on the 48 kHz signal, same 10 ms grid
  - power spectrum in dB (10·log10, floor at −120 dB) → real cepstrum via inverse FFT of the log-power spectrum
  - peak search in quefrency `1/600 s .. 1/60 s` (aligned with the f0 contract — v1's 60–500 Hz range conflicted)
  - linear regression of cepstrum magnitude vs quefrency over `1 ms .. 16.7 ms`, excluding the first 1 ms (low-quefrency spectral-envelope region)
  - CPP = cepstral peak (dB) − regression value at the peak quefrency
  - diagnostic + creak/breathiness evidence only in phase 0; not gameplay-facing until validated against the corpus. it earns its place because it's cheap, needs no pulse tracking, and is the best single periodicity/breathiness measure available at this cost.
- **H1–H2**: **deferred to phase 1.** uncorrected H1–H2 is confounded by F1 proximity, window leakage, and mic response; the ±10% harmonic search also collides with neighboring harmonics at higher f0. when it returns, it returns with Iseli–Alwan formant correction and stays diagnostic.
- **jitter / shimmer: removed from phase 0.** differences between overlapping YIN frame estimates measure estimator movement, vibrato, and glides — not cycle-to-cycle perturbation. real jitter/shimmer need pulse-synchronous period and amplitude extraction (phase 1, alongside the pulse-based creak detector). nothing in phase 0 may consume these values, which is why the creak rule in §3.3 no longer references them.

---

## 4. output frame

```csharp
public enum VoicingState : byte { Silence, Unvoiced, Voiced, Creak }

public readonly record struct AnalysisFrame
{
    // timing (§3.1)
    public long   WindowCenterSample   { get; init; }
    public long   ResultAvailableSample{ get; init; }
    public double TimeSeconds          { get; init; }  // WindowCenterSample / 48000.0

    // voicing
    public VoicingState Voicing        { get; init; }
    public float  VoicingConfidence    { get; init; }  // 0..1

    // pitch
    public float  F0RawHz              { get; init; }  // internal candidate, always logged
    public float  F0Hz                 { get; init; }  // published; NaN unless Voiced
    public float  F0DisplayHz          { get; init; }  // causal median + slew; NaN unless Voiced
    public float  F0Cents              { get; init; }  // re 55 Hz, from F0Hz
    public float  F0Confidence         { get; init; }
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
    public float  BrightnessProxy      { get; init; }
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

v1's "~43 ms" counted only the parts that are easy to count. the honest number is worse, and it still cannot be fixed by optimization — YIN needs 2–3 periods, and at 80 Hz that's 25–37 ms of audio before any answer *exists*.

this is why the game is a **tracing** game, not a **hitting** game. sustained lines and glides tolerate this; tight timing windows do not. **measure user-to-photon with the camera test (§3.1) on real hardware before committing to any chart design** — the loopback test only measures the audio half, and gates in §6 are on measured numbers, not this table.

---

## 6. batch mode and the test harness

this is the part that makes the difference between "seems fine on my voice" and "excellent."

### input

```
corpus/
  manifest.csv       speaker_id, file, device, os, condition, task, vowel,
                     target_f0, os_processing_flags (AGC/NS on|off|unknown),
                     consent_ref
  audio/*.wav        48 kHz mono (44.1 kHz accepted; resample logged)
```

### output

per-file Parquet on the frame schema, plus a run summary CSV, plus (offline only) the centered-median scoring column (§3.5).

### reference — layered, not "ground truth"

Praat agreement can just mean two systems share the same assumptions, especially for formants and creak. the reference stack:

1. **synthetic signals** with mathematically known f0 (and known formants via Klatt-style synthesis when we get to formant gates) — the only true ground truth we have.
2. **Praat via parselmouth** as a *versioned comparison baseline*: pin the Praat version and record every setting (pitch floor/ceiling, time step, max formant, LPC order) in the run summary. same 10 ms grid.
3. **hand-labeled hard subset**: creak, soft phonation, onsets/offsets, octave-error bait. labeled per a written annotation guide; creak labels from 2 annotators with inter-rater agreement reported. disagreement frames are excluded from gates and reported separately.
4. later, if available: EGG or acoustic pulse references.

**the causal live output is the product gate.** centered-offline numbers are reported separately and never substituted.

### metrics

| metric | definition | notes |
|---|---|---|
| GPE | % of ref-voiced frames where \|f0 − f0_ref\| / f0_ref > 0.20 | ref-creak frames excluded, reported separately |
| FPE | RMSE in cents on frames voiced in both and not GPE | |
| VDE | % frames with mismatched voiced/unvoiced decision | plus full 4-state confusion matrix (Silence/Unvoiced/Voiced/Creak) |
| voicing P/R | precision & recall for Voiced, and for Creak vs hand labels | |
| formant error | mean \|F − F_ref\| per formant (F1–F4 each), voiced frames | F3/F4 gated too — resonance estimates depend on them |
| p95 capture-to-result | measured | |
| user-to-photon | measured, camera test | reported, not gated in phase 0 |

**aspirational targets** (clean-slice): GPE < 2%, FPE < 15 cents, VDE < 5%, F1/F2 error < 60 Hz. but:

- **gates activate per slice only when the slice has ≥ 3 speakers and ≥ 10 files.** below that, metrics are reported with bootstrap 95% CIs and do not gate CI — worst-slice gating on a 2-file slice is a coin flip.
- the corpus is split **dev / held-out** (by speaker, not by file). thresholds are tuned on dev; the held-out set is scored untouched and reported alongside.
- until slices reach minimum size, CI gates are **regression gates**: no metric may worsen by more than its CI vs the last accepted run.

**compute everything per corpus slice, not just in aggregate.** a global pass rate hides failing entirely on gaming headsets. once slices are big enough, gate CI on the worst qualifying slice.

### corpus contents

- **conditions**: studio/condenser, laptop built-in, gaming headset, noisy room; with OS processing flags recorded (and disabled where possible)
- **tasks**: sustained /a/ /i/ /u/ at 5 pitch targets; ascending and descending glides; **soft/breathy sustained phonation** (near-floor level — this tests the §3.3 level gate); **resonance manipulation** (same speaker, same vowel, same pitch target, contrasted tract postures — e.g. instructed "bright/forward" vs neutral — repeated across devices and across sessions; this task feeds the game's resonance-signal gate, game doc §1.5); read passage (Rainbow Passage, public domain); spontaneous speech
- **adversarial**: deliberate creak, deliberate falsetto, breathy onset, whisper, cough, laugh, background music
- **speakers**: as wide an f0 range as recruitable — coverage across 80–350 Hz, multiple voice types, not just your own voice

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
latencyOffsetMs       (measured via loopback; user-to-photon measured separately)
f0SearchMinHz         (60)
f0SearchMaxHz         (600)
maxFormantHz          (5500 default; drives fs_a, LPC order, root range — §3.6)
```

---

## 8. build order

do not reorder these. each one de-risks the next. (v1's order had YIN before the streaming skeleton and synthetic harness; that's backwards — the contract and the tests are what make every later stage cheap to validate.)

1. **streaming skeleton + timing model.** `VoiceAnalyzer` shell, ring buffer, timestamps, `Reset`, diagnostics counters. chunk-boundary invariance test passes on a passthrough "analyzer."
2. **synthetic signal generator + test harness.** sines, harmonic complexes, glides, noise mixes. this is infrastructure for every later gate.
3. **capture → RMS meter.** confirms devices work, exercises the format policy. measure loopback latency now; run the camera test once for a baseline.
4. **YIN (causal) + voicing state machine.** validate against the synthetic sweep suite, then Praat on a few recorded files.
5. **batch mode + 20-file debug corpus + metrics script.** ← get here before formants and before octave-correction tuning. the metrics tell you whether anything works.
6. **octave correction + creak, tuned against the corpus.** grow the corpus; add hand labels for the hard subset.
7. **formants + resonance + brightness proxy (§3.7b).** re-run metrics with F1–F4 gates; score formants and proxy on the resonance-manipulation task.
8. **live visualization.** log-frequency pitch line plus a second resonance indicator — find out now whether two simultaneous tracked dimensions are legible or overwhelming.

**Gate A (analyzer viability) after step 6:** if GPE and VDE won't come down on the noisy, soft, and creaky slices, change approach while it's cheap. **Gate B (product readiness) after step 8** is owned by the game design doc: resonance-signal decision (formants vs §3.7b proxy on the resonance-manipulation task), two-dimension legibility, and the product latency thresholds — this spec measures p95 capture-to-result and user-to-photon and reports them; the thresholds on those numbers are product requirements and live in the game doc. deferred to phase 1: pulse-synchronous analysis (real jitter/shimmer, better creak), H1–H2 with formant correction, any user-facing VTL.

---

## 12. change log vs v1

accepted from the Sol review: streaming stateful API with chunk invariance (§1.1); voicing/creak contradiction fixed via explicit decision order and multi-frame creak (§3.3); jitter/shimmer removed as invalidly defined (§3.8); frame struct compile fix + timestamps + per-feature confidence (§4); latency semantics split into algorithmic/capture-to-result/user-to-photon (§3.1, §5); Praat demoted to versioned baseline within a layered reference stack (§6); exact YIN difference formula + tolerance-based sweep tests (§3.4); ring overflow policy (§2); `maxFormantHz` made to actually drive the pipeline (§3.6); formant track birth/death rules (§3.6); VTL rescoped to experimental batch aggregate (§3.7); dev/holdout split, slice minimums, confusion matrix (§6); device format + OS-processing policy (§3, §6); analysis/interpretation boundary and privacy requirements promoted to §0.

departures from the Sol review: **CPP stays in phase 0** — unlike jitter/shimmer it needs no pulse extraction, its v1 spec gaps were fixable (§3.8), and it's the cheapest honest breathiness/periodicity evidence we have, which the creak work wants anyway; it just stays diagnostic. build order also front-loads the synthetic harness before capture (Sol had capture first).

v2.2 (2026-08-19, from the second Sol review of the game design doc): resonance-manipulation corpus task added to §6; experimental brightness proxy specified (§3.7b) with a `BrightnessProxy` frame field; build-order gate renamed Gate A, with Gate B (product readiness, after step 8) defined in the game design doc — latency thresholds clarified as product requirements measured here but gated there.

new in v2 beyond both: level gate lowered to +8 dB with soft-phonation corpus coverage (v1's +12 dB would classify quiet breathy practice — a core use case — as silence); left-channel downmix rationale; causal-median settling counted honestly as perceived lag; creak detection decoupled from a valid f0 so sub-60 Hz creak still classifies.
