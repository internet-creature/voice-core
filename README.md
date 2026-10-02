# voice-core

VoiceCore is the voice analysis engine for a voice training game (Godot 4 + C#, for Steam). It is a streaming analyzer: audio buffers go in, and every 10 ms a frame comes out with pitch, voicing, level, formants and voice-quality measurements.

**Status:** phase 0, build step 5 of 8 (analyzer spec §8): YIN pitch tracking and the voicing state machine with a live pitch trace in the probe, and now batch mode over a real-recording debug corpus, with metrics and calibrated confidences. Frames carry timestamps, level, voicing, f0 and calibrated confidences; the display track, formants and voice quality are `NaN` until their steps land.

## Build

Requires the .NET 10 SDK.

```
dotnet test
```

- `VoiceCore/` is the analyzer library: no UI, no audio devices, no engine references. `VoiceCore.Streaming` holds the lock-free ring, frame queue, triple buffer and live analysis pump.
- `VoiceCore.Synthetic/` generates test signals with exact ground truth (sines, harmonic complexes, glides, noise, breathy voice). It also pairs analyzer frames with that truth and defines the spec §3.4 validation suites. It is test tooling and never ships in the game.
- `VoiceProbe.Capture/` handles microphone capture for the probe: PortAudio devices, the device format policy (48 kHz preferred, 44.1 kHz resampled, left channel of stereo), the loopback latency test, and session logs. It has no Godot dependency.
- `VoiceProbe/` is the Godot 4 probe app. It's disposable. See [Running the probe](#running-the-probe).
- `VoiceCore.Tests/` holds the xUnit tests, including chunk-boundary invariance and zero-allocation checks.
- `VoiceCore.Batch/` is batch mode. It runs the debug corpus, metrics and confidence calibration (see [The debug corpus](#the-debug-corpus)), analyzes single files, and exports synthetic signals to listen to or open in Praat:

  ```
  dotnet run --project VoiceCore.Batch -- synth list breathy
  dotnet run --project VoiceCore.Batch -- synth breathy/200Hz/hnr5/phase0 breathy.wav
  ```

## Docs

- [`docs/voice-analysis-spec.md`](docs/voice-analysis-spec.md) is phase 0. It covers the analyzer contract, signal chain, corpus, metrics and build order.
- [`docs/voice-game-design-doc.md`](docs/voice-game-design-doc.md) covers game design and the roadmap. It owns Gate B and the product thresholds.

The git history holds v1 → v2 of both docs, and each doc's change log explains the reasoning.

## Running the probe

Requires the **.NET build** of Godot 4.7 (`Godot_v4.7.2-stable_mono_win64`). The standard build can't run C# projects.

Open `VoiceProbe/project.godot` in the Godot editor and press Play. Then:

- **Pitch trace:** pick a capture path and a device, then press Start. On the native path, prefer the `[Windows WASAPI]` entries, since that's the only Windows host API where raw (unprocessed) capture can be requested.
  - The trace scrolls every analyzed frame on a log-frequency axis, with note and Hz gridlines. The line breaks where you're not voicing, and it fades with confidence.
  - The strip underneath shows the voicing state: green for Voiced, orange for Creak, grey for Unvoiced.
  - The readout shows Hz, the nearest note ± cents, and the confidence (a calibrated probability: P(not a gross pitch error), spec §3.9).
  - This is the tracker's raw output, with no smoothing yet (step 6), so glitches are visible on purpose.
- **Calibrate noise floor:** press it and stay quiet for 2 seconds (spec §3.2). The result is saved per device. The level trace shows the floor and the voicing gate.
- **● Record** (or the R key), next to Start: one click opens the mic if needed and starts recording, and the button turns red with a timer. The next click stops recording and leaves the mic running. Nothing is recorded unless you click it in this session (spec §0).
  - Recordings go to `%APPDATA%/Godot/app_userdata/VoiceProbe/recordings/` as `rec-<time>-<mic>.wav`, each with a `.txt` sidecar (device, format, noise floor, analyzer version).
  - **Two mics at once** (spec §6: one performance, several devices): open the probe twice and pick a different mic in each. The window title shows each window's mic, and the mic in the filename keeps the recordings apart.
  - "Delete all recordings" removes them after a confirmation.
- **Diagnostics tab:** capture-to-result (Gate B requires p95 < 10 ms), overruns, capture gaps, and the capture path comparison (Native vs Godot).
- **Latency tests tab:**
  - **Loopback:** plays tone bursts on an output and finds them on the selected input. Use VB-Cable for a repeatable baseline, or hold headphones or speakers to the mic for the acoustic path.
  - **Camera test:** flashes the screen on a clap. Film it at 240 fps; the frames between the clap and the flash are user-to-photon latency (spec §3.1).

Each session writes a log (device, format, raw-mode status, noise floor, diagnostics; never audio) to `%APPDATA%/Godot/app_userdata/VoiceProbe/sessions/`.

Headless checks, from `VoiceProbe/`:

```
godot_console --headless --path . --build-solutions --quit
godot_console --headless --path . -- --selftest=AT2020,3
godot_console --headless --path . -- "--loopback=CABLE Input,CABLE Output"
godot_console --headless --path . -- "--loopback=Realtek USB Audio,Insta360,1.2,voice"   # webcams: slow, noise-suppressed
```

## Analyzing recordings and comparing with Praat

```
dotnet run --project VoiceCore.Batch -c Release -- analyze rec.wav rec.csv
python -m venv tools/.venv && tools/.venv/Scripts/pip install praat-parselmouth   # once
tools/.venv/Scripts/python tools/praat_compare.py rec.wav rec.csv
```

`analyze` runs a 48 kHz WAV through the same streaming path as live capture and writes one CSV row per frame. It uses the recording's saved noise floor when there is one. `praat_compare.py` queries Praat at every frame's center and reports gross and fine f0 disagreement, voicing disagreement, and a state table. Praat is a versioned *comparison baseline* (spec §6), not ground truth, so the script prints its version and every setting.

## The debug corpus

Real recordings are the product gate (spec §6). The corpus lives in `corpus/`, which is **gitignored**: voice audio never goes in this public repo. [`docs/corpus.md`](docs/corpus.md) has the details.

```
tools/.venv/Scripts/python tools/fetch_ptdb.py          # 80 PTDB-TUG files, 20 speakers, laryngograph references
dotnet run --project VoiceCore.Batch -c Release -- corpus add <probe-recording.wav> --task siren
tools/.venv/Scripts/python tools/praat_reference.py     # Praat references for your own recordings
dotnet run --project VoiceCore.Batch -c Release -- corpus run        # -> corpus/runs/<time>/report.md
dotnet run --project VoiceCore.Batch -c Release -- corpus calibrate  # refit VoiceCore/FittedCalibration.cs on dev
```

PTDB-TUG (Graz University of Technology) is used under the Open Database License 1.0 / Database Contents License 1.0 and isn't redistributed.

## Principles

- **Measure, never interpret.** VoiceCore reports acoustics. It never classifies a voice by gender, and targets are always chosen by the user.
- **Local-first.** No audio leaves the machine. Voice measurements never appear in telemetry.
- **Honest abstention.** `NaN` and "tracking unreliable" beat a wrong number.
