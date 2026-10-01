"""Write Praat f0 references for corpus files that have no other reference
(analyzer spec §6, reference layer 2).

Praat is a *versioned comparison baseline*, not ground truth. Use it for files
with no laryngograph reference, such as your own probe recordings. The header of
each reference records the Praat version and every setting, and `corpus run`
copies it into the run summary.

usage:
    python tools/praat_reference.py [--corpus corpus] [--force]

Fills the manifest's `reference` column for rows where it's empty, writing
reference/<stem>.praat.csv on Praat's own 10 ms grid. VoiceCore.Batch
interpolates references onto its frame centers.
"""

import argparse
import csv
import os
import sys

import parselmouth

SETTINGS = dict(
    time_step=0.01,
    pitch_floor=60.0,  # = AnalysisConfig.F0SearchMinHz
    max_number_of_candidates=15,
    very_accurate=False,
    silence_threshold=0.03,
    voicing_threshold=0.45,
    octave_cost=0.01,
    octave_jump_cost=0.35,
    voiced_unvoiced_cost=0.14,
    pitch_ceiling=1000.0,  # = AnalysisConfig.F0SearchMaxHz
)


def write_reference(wav, out):
    sound = parselmouth.Sound(wav)
    if sound.n_channels > 1:
        sound = sound.extract_left_channel()  # same downmix policy as VoiceCore (§3)
    pitch = sound.to_pitch_ac(**SETTINGS)
    f0 = pitch.selected_array["frequency"]
    with open(out, "w", newline="", encoding="utf-8") as f:
        f.write(f"# source: Praat {parselmouth.PRAAT_VERSION} ({parselmouth.PRAAT_VERSION_DATE}) via parselmouth {parselmouth.VERSION}, to_pitch_ac: "
                + ", ".join(f"{k}={v}" for k, v in SETTINGS.items()) + "\n")
        f.write("# time: Praat frame centers\n")
        f.write("# states: binary (Voiced/Unvoiced; Unvoiced includes silence)\n")
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["time_s", "state", "f0_hz"])
        for t, hz in zip(pitch.xs(), f0):
            w.writerow([f"{t:.5f}", "Voiced" if hz > 0 else "Unvoiced", f"{hz:.3f}" if hz > 0 else ""])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", default="corpus")
    ap.add_argument("--force", action="store_true", help="regenerate existing Praat references too")
    args = ap.parse_args()

    manifest = os.path.join(args.corpus, "manifest.csv")
    with open(manifest, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        columns, rows = reader.fieldnames, list(reader)

    os.makedirs(os.path.join(args.corpus, "reference"), exist_ok=True)
    written = 0
    for r in rows:
        ref = r.get("reference", "")
        if ref and not (args.force and ref.endswith(".praat.csv")):
            continue
        stem = os.path.splitext(os.path.basename(r["file"]))[0]
        ref = f"reference/{stem}.praat.csv"
        write_reference(os.path.join(args.corpus, r["file"]), os.path.join(args.corpus, ref))
        r["reference"] = ref
        written += 1
        print(ref, flush=True)

    with open(manifest, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=columns, lineterminator="\n")
        w.writeheader()
        w.writerows(rows)
    print(f"{written} Praat references written (Praat {parselmouth.PRAAT_VERSION})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
