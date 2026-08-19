# voice analysis prototype — technical spec

phase 0 deliverable. the goal is not a game. the goal is to answer "is the tracking good enough to build on?" with numbers, before any engine work happens.

---

## 1. solution layout

three projects. the split matters more than anything else in this doc.

```
VoiceCore/           net8.0 class library
  - zero UI, zero audio-device code, zero platform deps
  - pure function: float[] in, AnalysisFrame[] out
  - this is the assembly Godot will reference later, unchanged

VoiceCore.Batch/     console app
  - runs VoiceCore over a corpus of WAVs
  - emits per-frame Parquet + summary CSV

VoiceProbe/          Avalonia app
  - live mic capture, minimal render, frame logging
  - disposable. do not put logic here.

VoiceCore.Tests/     xUnit
```

**VoiceCore must never know what a microphone is.** it takes buffers. the probe and the game both feed it. if you get this wrong you'll rewrite it when you move to Godot.

### dependencies

| package | project | license |
|---|---|---|
| PortAudioSharp2 (or NAudio if Windows-only) | VoiceProbe | MIT |
| FftSharp | VoiceCore | MIT |
| MathNet.Numerics (eigenvalues for LPC roots) | VoiceCore | MIT |
| Parquet.Net | VoiceCore.Batch | Apache-2.0 |
| Avalonia + SkiaSharp | VoiceProbe | MIT |

all permissive. if you want VoiceCore dependency-free, hand-roll the real FFT (~100 lines) and use Bairstow's method for polynomial roots instead of MathNet.

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

---

## 3. signal chain

input contract: **48 kHz, mono, float32, normalized -1..1**.

hop size **480 samples (10 ms)**. every stage reports on this grid.

### 3.1 preprocessing

- **DC removal**: one-pole high-pass, `y[n] = x[n] - x[n-1] + 0.995 * y[n-1]`
- **no AGC on the analysis path.** it would destroy your level measurements. if you want a monitoring path with AGC, make it a separate branch.
- **RMS** over the hop → dBFS
- **peak** over the hop → dBFS, plus a clipping flag at > -0.1 dBFS
- **noise floor**: 10th percentile of RMS over a rolling 5 s window, captured during calibration and persisted

### 3.2 voicing decision

make this its own stage. do not infer it from whether the pitch detector returned something.

inputs:
- RMS > noise floor + 12 dB
- YIN aperiodicity (the CMND value at the chosen lag) < 0.20
- zero-crossing rate below a threshold

output: `Silence | Unvoiced | Voiced | Creak` plus a 0..1 confidence.

### 3.3 f0 — YIN-FFT

- window **2048 samples** (42.7 ms @ 48 kHz)
- search range **60–600 Hz** → lag τ from 80 to 800 samples
- steps:
  1. difference function `d(τ)` computed via FFT autocorrelation
  2. cumulative mean normalized difference `d'(τ)`
  3. absolute threshold 0.15 — take the **first** local minimum below it; if none, take the global minimum and mark low confidence
  4. parabolic interpolation over the three points around the minimum for sub-sample lag
- output `f0Hz`, and `f0Cents = 1200 * log2(f0 / 55.0)` — fixed 55 Hz anchor so the number is stable across sessions

do the FFT version, not naive YIN. naive is ~1.5 M ops per hop; the FFT version is roughly 50× cheaper.

**sanity check before anything else:** feed it a synthesized 200 Hz sine. it should return 200.00 Hz. if it doesn't, stop and fix it.

### 3.4 octave error correction

this is where naive implementations quietly fail. budget real time here.

```
1. keep a running median of the last 5 valid f0Cents values
2. if |cents[n] - runningMedian| is between 1100 and 1300:
     - evaluate d'(τ/2) and d'(τ*2)
     - if the alternate candidate's d' is within 0.05 of the chosen one,
       prefer the candidate closer to the running median
3. median filter, width 5:
     - CAUSAL median (last 5 frames) for live display  → 0 added latency
     - CENTERED median (±2 frames) for offline scoring → better, 20 ms latency
4. slew limit 1200 cents/s — DISPLAY ONLY. log the raw value.
5. never smooth across a voicing gap. reset the filter state.
```

### 3.5 creak detection

report creak as creak. do not report a bogus 55 Hz — that's a lie the user will catch, and it destroys trust in every other number you show.

MVP detector:

```
f0 < 75 Hz  AND  (jitterLocal > 0.05  OR  aperiodicity > 0.30)
    → VoicingState.Creak, f0 = NaN
```

better version later: peak-pick glottal pulses on the lowpassed residual, compute inter-pulse intervals, flag when the coefficient of variation exceeds 0.15.

transfem practice generates enormous amounts of creak on descents and at the bottom of range. this is a high-traffic code path, not an edge case.

### 3.6 formants — LPC

**voiced frames only.**

- **resample 48 kHz → 16 kHz** (exact 3× decimation; anti-alias with a Kaiser-windowed FIR lowpass at 7.6 kHz)
  - do not run LPC at 48 kHz. you'd spend every pole modelling energy above the formant range.
- pre-emphasis `y[n] = x[n] - 0.97 * x[n-1]`
- window 25 ms Hamming (400 samples @ 16 kHz), hop 10 ms (160 samples)
- autocorrelation → Levinson-Durbin → LPC coefficients
- **order 18** (rule of thumb: `2 + fs_kHz`)
- root-find the LPC polynomial via companion-matrix eigenvalues
- convert roots to formants:
  - `F = (fs / 2π) * |angle(z)|`
  - `BW = -(fs / π) * ln|z|`
- keep roots with `90 Hz < F < 7500 Hz`, `BW < 400 Hz`, positive imaginary part; sort ascending → F1..F4
- **continuity tracking**: nearest-neighbour assignment to the previous frame's formants with a jump penalty. without this your labels will swap and your F2 trace will look like noise.

`maxFormant` should be a calibration parameter, not a constant — the appropriate value differs across your user population.

### 3.7 resonance estimate

two outputs, both per-frame plus a session median:

- **formant dispersion**: `Df = mean(F(i+1) - F(i))` for i = 1..3
- **VTL estimate**: `VTL ≈ c / (2 * Df)` with `c = 35000 cm/s`

weight F3 and F4 in anything speaker-level. F1 and F2 are dominated by which vowel is being produced — any measure built on them is only comparable **within a fixed vowel**. tag every frame with the exercise's expected vowel so batch analysis can group correctly.

### 3.8 voice quality

- **H1–H2 (dB)**: locate harmonics using the detected f0 — search ±10% around `n * f0` for the local spectral max. note Iseli-Alwan formant correction as future work.
- **CPP (dB)**: real cepstrum of a 40 ms window; find the peak in the quefrency range corresponding to 60–500 Hz; measure prominence above a linear regression fit of the cepstrum over that range. best single breathiness/periodicity measure you can compute cheaply.
- **jitter (local)**: mean absolute difference of consecutive periods / mean period. periods come from the YIN lag track.
- **shimmer (local)**: same, on peak amplitudes.

---

## 4. output frame

```csharp
public readonly struct AnalysisFrame
{
    public long   SampleIndex;       // first sample of the hop
    public double TimeSeconds;

    public VoicingState Voicing;     // Silence | Unvoiced | Voiced | Creak
    public float  Confidence;        // 0..1

    public float  F0Hz;              // NaN unless Voiced
    public float  F0Cents;           // re 55 Hz
    public float  Aperiodicity;      // YIN d' at chosen lag

    public float  RmsDbfs;
    public float  PeakDbfs;
    public bool   Clipping;

    public float  F1Hz, F2Hz, F3Hz, F4Hz;   // NaN unless Voiced
    public float  B1Hz, B2Hz, B3Hz, B4Hz;
    public float  FormantDispersionHz;
    public float  VtlEstimateCm;

    public float  H1H2Db;
    public float  CppDb;
    public float  JitterLocal;
    public float  ShimmerLocal;
}

public enum VoicingState { Silence, Unvoiced, Voiced, Creak }
```

use `NaN` for "not measured", never `0`. a zero will silently poison every average you compute downstream.

---

## 5. latency budget

| stage | cost |
|---|---|
| capture buffer (256 samples) | 5.3 ms |
| YIN window centering | 21.3 ms |
| causal median (live) | 0 ms |
| render at 60 fps | 16.7 ms |
| **total, live display** | **~43 ms** |

that is above the ~30 ms threshold where visual feedback reads as instantaneous, and it cannot be fixed — YIN needs 2–3 periods, and at 80 Hz that's 25–37 ms of audio before any answer exists.

this is why the game is a **tracing** game, not a **hitting** game. sustained lines and glides tolerate 40 ms. tight timing windows do not. measure the real number with a loopback test before committing to any chart design.

---

## 6. batch mode and the test harness

this is the part that makes the difference between "seems fine on my voice" and "excellent."

### input

```
corpus/
  manifest.csv       speaker_id, file, device, condition, task, vowel, target_f0
  audio/*.wav        48 kHz mono
```

### output

per-file Parquet on the frame schema above, plus a run summary CSV.

### reference

run Praat via `parselmouth` in Python over the same files on the same 10 ms grid. that's your ground truth.

### metrics

| metric | definition | initial gate |
|---|---|---|
| GPE | % of ref-voiced frames where \|f0 − f0_ref\| / f0_ref > 0.20 | < 2% |
| FPE | RMSE in cents on frames voiced in both and not GPE | < 15 cents |
| VDE | % of frames with mismatched voiced/unvoiced decision | < 5% |
| formant error | mean \|F − F_ref\| per formant, voiced frames | < 60 Hz (F1–F2) |
| creak F1 | against hand labels | report only at first |
| p95 latency | measured, not calculated | < 50 ms |

**compute these per corpus slice, not just in aggregate.** a global pass rate hides the fact that you're failing entirely on gaming headsets. gate CI on the worst slice.

### corpus contents

- **conditions**: studio/condenser, laptop built-in, gaming headset, noisy room
- **tasks**: sustained /a/ /i/ /u/ at 5 pitch targets; ascending and descending glides; read passage (the Rainbow Passage is public domain and standard in voice research); spontaneous speech
- **adversarial**: deliberate creak, deliberate falsetto, breathy onset, whisper, cough, laugh, background music
- **speakers**: as wide an f0 range as you can recruit — you need coverage across the whole 80–350 Hz span, not just your own voice

start with 20 files. it'll find bugs immediately. grow it as you go.

---

## 7. calibration parameters

persisted, settable, exposed in the probe UI:

```
inputDevice
sampleRate            (48000)
inputGainDb
noiseFloorDbfs        (measured)
latencyOffsetMs       (measured via loopback)
f0SearchMinHz         (60)
f0SearchMaxHz         (600)
maxFormantHz          (per-user)
```

---

## 8. build order

do not reorder these. each one de-risks the next.

1. **capture → RMS meter.** confirms devices work. measure loopback latency now.
2. **YIN → f0 to console.** validate against a synthesized sine (exact), then against Praat on one recorded file.
3. **voicing + octave correction + creak.**
4. **batch mode + first 20-file corpus + metrics script.** ← get here before formants. the metrics are what tell you whether anything works.
5. **formants + resonance.** re-run metrics.
6. **live visualization.** log-frequency pitch line plus a second resonance indicator — the point is to find out now whether two simultaneous tracked dimensions are legible or overwhelming.

the decision gate is after step 4. if GPE and VDE won't come down on the noisy and creaky slices, that's a signal to change the approach while it's still cheap.
