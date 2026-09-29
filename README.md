# voice-core

VoiceCore is the voice analysis engine for a voice training game (Godot 4 + C#, for Steam). It is a streaming analyzer: audio buffers go in, and every 10 ms a frame comes out with pitch, voicing, level, formants and voice-quality measurements.

**Status:** design phase. No code yet.

## Docs

- [`docs/voice-analysis-spec.md`](docs/voice-analysis-spec.md) is phase 0. It covers the analyzer contract, signal chain, corpus, metrics and build order.
- [`docs/voice-game-design-doc.md`](docs/voice-game-design-doc.md) covers game design and the roadmap. It owns Gate B and the product thresholds.

The git history holds v1 → v2 of both docs, and each doc's change log explains the reasoning.

## Principles

- **Measure, never interpret.** VoiceCore reports acoustics. It never classifies a voice by gender, and targets are always chosen by the user.
- **Local-first.** No audio leaves the machine. Voice measurements never appear in telemetry.
- **Honest abstention.** `NaN` and "tracking unreliable" beat a wrong number.
