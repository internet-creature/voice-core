# voice-core

VoiceCore is the voice analysis engine for a voice training game (Godot 4 + C#, for Steam). It is a streaming analyzer: audio buffers go in, and every 10 ms a frame comes out with pitch, voicing, level, formants and voice-quality measurements.

**Status:** phase 0, build step 3 of 8 (analyzer spec §8): streaming skeleton, timing model, synthetic test harness, and the Godot capture probe with a level meter and latency tests. Frames carry timestamps and level; pitch, voicing, formants and the rest are `NaN` until their steps land.

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
- `VoiceCore.Batch/` will be batch mode (step 5). For now it exports synthetic signals to listen to or open in Praat:

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

- **Level meter:** pick a capture path and a device, then press Start. On the native path, prefer the `[Windows WASAPI]` entries: that's the only Windows host API where raw (unprocessed) capture can be requested.
- **Capture path comparison:** switch between Native (PortAudio) and Godot (AudioEffectCapture). The diagnostics show capture-to-result for each; Gate B requires p95 < 10 ms.
- **Loopback test:** plays tone bursts on an output and finds them on the selected input. Use VB-Cable (CABLE Input → CABLE Output) for a repeatable baseline, or speakers into the mic for the acoustic path.
- **Camera test:** tick "flash the screen on a clap", then film yourself clapping next to the screen with a 240 fps phone camera. The frames between the clap and the flash are user-to-photon latency (spec §3.1).

Each session writes a log (device, format, raw-mode status, diagnostics; never audio) to `%APPDATA%/Godot/app_userdata/VoiceProbe/sessions/`.

Headless checks, from `VoiceProbe/`:

```
godot_console --headless --path . --build-solutions --quit
godot_console --headless --path . -- --selftest=AT2020,3
godot_console --headless --path . -- "--loopback=CABLE Input,CABLE Output"
```

## Principles

- **Measure, never interpret.** VoiceCore reports acoustics. It never classifies a voice by gender, and targets are always chosen by the user.
- **Local-first.** No audio leaves the machine. Voice measurements never appear in telemetry.
- **Honest abstention.** `NaN` and "tracking unreliable" beat a wrong number.
