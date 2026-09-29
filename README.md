# voice-core

VoiceCore is the voice analysis engine for a voice training game (Godot 4 + C#, for Steam). It is a streaming analyzer: audio buffers go in, and every 10 ms a frame comes out with pitch, voicing, level, formants and voice-quality measurements.

**Status:** phase 0, build step 1 of 8 (analyzer spec §8): streaming skeleton and timing model. Frames carry timestamps and level; pitch, voicing, formants and the rest are `NaN` until their steps land.

## Build

Requires the .NET 10 SDK.

```
dotnet test
```

- `VoiceCore/` is the analyzer library: no UI, no audio devices, no engine references. `VoiceCore.Streaming` holds the lock-free ring, frame queue, triple buffer and live analysis pump.
- `VoiceCore.Tests/` holds the xUnit tests, including chunk-boundary invariance and zero-allocation checks.
- `VoiceCore.Batch/` is a placeholder for batch mode (step 5).

## Docs

- [`docs/voice-analysis-spec.md`](docs/voice-analysis-spec.md) is phase 0. It covers the analyzer contract, signal chain, corpus, metrics and build order.
- [`docs/voice-game-design-doc.md`](docs/voice-game-design-doc.md) covers game design and the roadmap. It owns Gate B and the product thresholds.

The git history holds v1 → v2 of both docs, and each doc's change log explains the reasoning.

## Principles

- **Measure, never interpret.** VoiceCore reports acoustics. It never classifies a voice by gender, and targets are always chosen by the user.
- **Local-first.** No audio leaves the machine. Voice measurements never appear in telemetry.
- **Honest abstention.** `NaN` and "tracking unreliable" beat a wrong number.
