# The debug corpus

How to build, run and read the analyzer's real-recording corpus (analyzer spec §6, build step 5). The spec says *what* is measured. This page says how the tooling does it and which choices it makes where the spec leaves room.

**Privacy first (spec §0).** The corpus lives in `corpus/` at the repo root, which is **gitignored**. The repo is public. Never commit voice audio, references, frames or run reports derived from a person's voice, and never `git add -f` anything under `corpus/`. Every human recording needs a consent record in `corpus/consent/`, referenced from the manifest.

## Layout

```
corpus/
  manifest.csv     one row per recording (below)
  splits.csv       speaker_id,split   — dev or heldout, by speaker (unlisted speakers are dev)
  consent/         consent and usage-rights records, one per source or speaker
  audio/           48 kHz WAVs (probe recordings keep their .txt sidecar)
  reference/       f0/voicing reference tracks, one per file
  labels/          optional hand labels, <stem>.csv
  raw/             original reference files as downloaded (e.g. PTDB-TUG .f0)
  runs/<time>/     report.md, summary.csv, frames/<stem>.parquet
  accepted/        the regression baseline: summary.csv + run.txt
```

### manifest.csv

The spec §6 columns, plus two:

| column | meaning |
|---|---|
| speaker_id, file, source, session_id, device, os, condition, task, vowel, target_f0, os_processing_flags, consent_ref | spec §6 |
| `noise_floor_dbfs` | the session's calibrated floor (§3.2). Blank: estimated from the file as the 10th percentile of hop RMS, the live calibration's statistic over the whole file. |
| `reference` | path of the reference track, relative to `corpus/` |

### Reference tracks

```
# source: <what produced it: tool, version, every setting>
# time: <how the times were aligned>
# states: binary (Voiced/Unvoiced; Unvoiced includes silence)
time_s,state,f0_hz
0.0220,Unvoiced,
0.0320,Voiced,112.400
```

Any time grid works. The analyzer's frames are scored at their window centers: f0 is interpolated on a log scale between two voiced points, and at a voicing boundary the nearer point wins. A frame more than half a hop past either end of the reference isn't scored.

A **binary** reference (Praat, laryngograph RAPT) knows voiced vs not. Its Unvoiced covers silence, and it can't say whether a Creak frame was right.

### Hand labels (reference layer 3)

`labels/<stem>.csv`: `start_s,end_s,label`, with labels `Silence`, `Unvoiced`, `Voiced`, `Creak` or `Exclude`. A label overrides the reference's state for the frames it covers and makes them four-state. A `Voiced` label keeps the reference's f0. `Exclude` frames (annotator disagreement) are left out of every metric and counted separately. The annotation guide and the two-annotator creak labels come with step 6.

## Sources so far

**PTDB-TUG** (reference layer 4): `tools/fetch_ptdb.py` downloads 4 sentences × 20 speakers (10 female, 10 male) of studio headset read speech, each with a laryngograph-derived reference. That gives real f0 ground truth from day one, and a speaker-independent held-out split: F08–F10 and M08–M10 are held out.

- License: Open Database License 1.0 / Database Contents License 1.0. Development use; never redistributed.
- Format: Snack ESPS/RAPT output (f0, voicing, RMS, cross-correlation), 32 ms window, 10 ms hop.
- **Time alignment, measured:** frame *i* describes *i* × 10 ms + 22 ms. The corpus doesn't document this, so the offset was found by minimizing disagreement with **Praat on the mic signal** (not VoiceCore, so the reference isn't aligned to the analyzer it judges). The minimum over 12 files from 12 speakers is at 21–23 ms.
- **Its fine-error floor:** Praat scored against this reference gets **FPE 25 cents RMS** (median |error| 8 cents), GPE 1.6% (dev) / 3.2% (held-out) and VDE 4.9% / 3.7%. So FPE against this reference measures the reference as much as the analyzer. That's why the FPE < 15 cents target is reported but not gated (spec v2.5).

**Your own recordings** (reference layer 2, Praat), through the probe:

1. Probe: Start, Calibrate noise floor, turn on Record audio, perform the task, turn recording off. Files land in `%APPDATA%/Godot/app_userdata/VoiceProbe/recordings/`.
2. `dotnet run --project VoiceCore.Batch -c Release -- corpus add <rec.wav> --task <task>` copies the file and its sidecar in. Device, noise floor and raw-mode status come from the sidecar, and the consent record is created on first use. Tasks are the spec §6 list: `sustained` (add `--vowel a --target-f0 220`), `siren`, `glide`, `read`, `spontaneous`, `breathy`, `creak`, `falsetto`, `resonance`, ...
3. `tools/.venv/Scripts/python tools/praat_reference.py` writes a Praat reference for every file that has none and records the Praat version and every setting in its header.

A new speaker is added to `splits.csv` as dev. A single speaker is always dev: one person can't be both sides of a speaker split.

## Commands

```
dotnet run --project VoiceCore.Batch -c Release -- corpus run [--gate]
dotnet run --project VoiceCore.Batch -c Release -- corpus accept [<run>]
dotnet run --project VoiceCore.Batch -c Release -- corpus calibrate
```

- **run**: every manifest file goes through the same streaming analyzer as live capture (causal output only) and is scored against its reference. Writes `runs/<time>/report.md` (read this), `summary.csv` (every metric × slice × split with bootstrap 95% CIs) and `frames/<stem>.parquet` (the §4 frame schema plus `ref_state` and `ref_f0_hz`; analyzer version, config hash and calibration in the file metadata). With `--gate`, it exits 1 on a qualifying-slice target miss or a regression.
- **accept**: makes a run (default: the latest) the regression baseline.
- **calibrate**: fits the §3.9 confidence calibration on the **dev split only** and writes `VoiceCore/FittedCalibration.cs`. Then bump `AnalysisConfig.AnalyzerVersion`, rebuild, `corpus run` to score it on held-out, and commit the generated file. The fitted knots are aggregate statistics (no audio or per-frame measurements), and the file names the corpus it was fit on.

## How each metric is computed

The spec §6 definitions, with these choices made explicit:

- **GPE**: over frames where the reference is voiced with an f0 inside the search range (60–1000 Hz) **and** the analyzer published Voiced. Error over 20% is gross. A Voiced frame with no f0 (`Above`/`Below`) on an in-range reference also counts as gross. Frames we failed to voice show up in VDE, Voiced recall and coverage, not GPE.
- **FPE**: RMS cents over the both-voiced, non-gross frames. Also reported: the **median |fine|** (1-cent resolution) and the **fine bias** (|mean signed error|; a timing or tuning offset shows up here).
- **VDE**: frames where published Voiced ≠ reference Voiced. Creak counts as not voiced on both sides.
- **Confusion matrix**: published state × reference state, with a fifth column for a binary reference's "not voiced".
- **Voiced / Creak precision and recall**: from the confusion matrix. Creak precision only counts hand-labeled frames, so it's n/a until step 6.
- **Confidence calibration**: ten equal-width bins, with predicted mean vs observed rate and ECE. `F0Confidence` is right when the frame isn't a gross error, judged only where both sides have an in-range pitch. A Voiced frame on an unvoiced reference is a voicing error, judged by `VoicingConfidence`. `VoicingConfidence` is right when the published state matches the reference, per published state. A binary reference can't judge Creak.
- **Coverage at floor**: reference-voiced in-range frames published Voiced with both confidences ≥ 0.5 / 0.8 / 0.9.
- **Out-of-range folds**: reference f0 outside 60–1000 Hz, published with an in-range F0Hz.
- **Slices**: all, source, condition, task, and f0 band (< 150, 150–250, > 250 Hz). For the band slices, voiced frames go by their reference f0, and other frames go by the file's median reference f0, so VDE has a band too. Each slice is reported separately for dev and held-out.
- **CIs**: bootstrap by **file** (1000 resamples, fixed seed), since frames within a file are correlated.
- **Gates**: a slice qualifies at ≥ 3 speakers and ≥ 10 files. Qualifying slices are held to GPE < 2% and VDE < 5%. Every metric is held to the regression rule: it may not worsen by more than its CI half-width vs the accepted run.

## Not yet

- 44.1 kHz input is rejected, not resampled (no corpus file needs it yet).
- No centered-offline scoring column: it needs §3.5 (step 6).
- Creak calibration, Creak precision and recall: they need hand labels (step 6).
- The soft/breathy coverage gate (Gate A) needs breathy recordings, which public corpora don't have.
