"""Compare VoiceCore's per-frame f0 with Praat's (analyzer spec §6, reference layer 2).

Praat is a *versioned comparison baseline*, not ground truth: agreement can just
mean two systems share assumptions. The version and every pitch setting are
printed so a run can be reproduced.

usage:
    python tools/praat_compare.py <recording.wav> <voicecore.csv> [--ceiling 1000] [--floor 60]

The CSV comes from `VoiceCore.Batch analyze`. Praat is queried at each VoiceCore
frame's window center, so both describe the same moments.
"""

import argparse
import csv
import math
import sys

import parselmouth


def load_voicecore(path):
    frames = []
    with open(path, newline="", encoding="utf-8") as f:
        rows = csv.DictReader(line for line in f if not line.startswith("#"))
        for row in rows:
            f0 = float(row["f0_hz"]) if row["f0_hz"] else math.nan
            frames.append((float(row["time_s"]), row["voicing"], f0))
    return frames


def praat_pitch(wav_path, floor, ceiling, time_step):
    sound = parselmouth.Sound(wav_path)
    if sound.n_channels > 1:
        sound = sound.extract_left_channel()  # same downmix policy as VoiceCore (§3)
    settings = dict(
        time_step=time_step,
        pitch_floor=floor,
        max_number_of_candidates=15,
        very_accurate=False,
        silence_threshold=0.03,
        voicing_threshold=0.45,
        octave_cost=0.01,
        octave_jump_cost=0.35,
        voiced_unvoiced_cost=0.14,
        pitch_ceiling=ceiling,
    )
    return sound.to_pitch_ac(**settings), settings


def cents(a, b):
    return 1200 * math.log2(a / b)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("wav")
    ap.add_argument("csv")
    ap.add_argument("--floor", type=float, default=60.0)
    ap.add_argument("--ceiling", type=float, default=1000.0)
    ap.add_argument("--time-step", type=float, default=0.01)
    args = ap.parse_args()

    ours = load_voicecore(args.csv)
    pitch, settings = praat_pitch(args.wav, args.floor, args.ceiling, args.time_step)

    both = gross = 0
    fine_cents = []
    mismatch = 0
    confusion = {}  # (voicecore state, praat voiced?) -> count
    for t, state, f0 in ours:
        p = pitch.get_value_at_time(t)
        praat_voiced = not math.isnan(p)
        ours_voiced = state == "Voiced" and not math.isnan(f0)
        confusion[(state, praat_voiced)] = confusion.get((state, praat_voiced), 0) + 1
        if ours_voiced != praat_voiced:
            mismatch += 1
        if ours_voiced and praat_voiced:
            both += 1
            if abs(f0 / p - 1) > 0.20:
                gross += 1
            else:
                fine_cents.append(cents(f0, p))

    n = len(ours)
    print(f"Praat {parselmouth.PRAAT_VERSION} ({parselmouth.PRAAT_VERSION_DATE}) via parselmouth {parselmouth.VERSION}")
    print("Praat pitch (ac): " + ", ".join(f"{k}={v}" for k, v in settings.items()))
    print(f"frames: {n}  both voiced: {both}")
    if both:
        rms = math.sqrt(sum(c * c for c in fine_cents) / len(fine_cents)) if fine_cents else math.nan
        mean = sum(fine_cents) / len(fine_cents) if fine_cents else math.nan
        print(f"gross disagreement (>20%, GPE vs Praat): {gross / both:.2%} ({gross}/{both})")
        print(f"fine disagreement: RMS {rms:.1f} cents, mean {mean:+.1f} cents (VoiceCore minus Praat)")
    print(f"voicing disagreement (VDE vs Praat): {mismatch / n:.2%} ({mismatch}/{n})")
    print("VoiceCore state vs Praat voiced:")
    for state in ("Voiced", "Unvoiced", "Creak", "Silence"):
        v = confusion.get((state, True), 0)
        u = confusion.get((state, False), 0)
        if v or u:
            print(f"  {state:9s} praat voiced {v:5d}   praat unvoiced {u:5d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
