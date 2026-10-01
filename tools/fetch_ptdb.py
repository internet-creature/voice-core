"""Add a PTDB-TUG subset to the debug corpus (analyzer spec §6, reference layer 4).

PTDB-TUG (Graz University of Technology) has headset-mic recordings of 20 native
English speakers (10 female, 10 male) with laryngograph-derived reference pitch.
That's real f0 ground truth from speakers other than the developer, which is what
gives the corpus a speaker-independent held-out split from day one.

License: Open Database License 1.0, contents under the Database Contents License
1.0 (https://www.spsc.tugraz.at/databases-and-tools/ptdb-tug-pitch-tracking-database-from-graz-university-of-technology.html).
Development use is fine. The files are never committed or redistributed: corpus/
is gitignored.

usage:
    python tools/fetch_ptdb.py [--corpus corpus] [--sentences sa1,sa2,sx+0,sx+1]

Each file is downloaded individually (no 3.9 GB zip). Re-running skips files that
are already there and manifest rows that already exist.

Reference format notes (measured, not documented by the corpus):
- the .f0 files are Snack ESPS/RAPT output, 4 columns: f0 Hz, voicing probability
  (0 or 1), RMS, peak normalized cross-correlation. 32 ms window, 10 ms hop.
- frame i describes time i*0.010 + 0.022 s. The offset was measured by
  minimizing the f0 disagreement with Praat run on the mic signal over 12 files
  from 12 speakers (minimum at 21-23 ms, flat to about 0.1 cents). Praat, not
  VoiceCore, so the reference isn't aligned to the analyzer it judges.
- even at that offset, Praat and the laryngograph reference differ by ~16 cents
  mean absolute on voiced frames. Treat FPE against this reference as having a
  floor of that order.
"""

import argparse
import csv
import os
import sys
import urllib.parse
import urllib.request

BASE = "https://www2.spsc.tugraz.at/databases/PTDB-TUG/SPEECH%20DATA"
TIME_OFFSET_S = 0.022
HOP_S = 0.010

# first sx sentence per speaker number (DOCUMENTATION/SPEAKER-PROFILES.txt); the
# female and male speaker with the same number read the same sentences
SX_START = {1: 3, 2: 48, 3: 93, 4: 138, 5: 183, 6: 228, 7: 273, 8: 318, 9: 363, 10: 408}

# by speaker, never by file (spec §6). Fixed so runs stay comparable.
HELDOUT = {"F08", "F09", "F10", "M08", "M09", "M10"}

MANIFEST_COLUMNS = [
    "speaker_id", "file", "source", "session_id", "device", "os", "condition", "task",
    "vowel", "target_f0", "os_processing_flags", "consent_ref", "noise_floor_dbfs", "reference",
]

CONSENT = """# PTDB-TUG usage rights

- source: Pitch Tracking Database from Graz University of Technology (PTDB-TUG),
  https://www.spsc.tugraz.at/databases-and-tools/ptdb-tug-pitch-tracking-database-from-graz-university-of-technology.html
- license: Open Database License 1.0 (database), Database Contents License 1.0 (contents)
- speaker consent: each speaker signed a declaration of allowance for use of the
  recordings (PTDB-TUG report, section 3)
- use here: development measurement only (f0/voicing metrics, confidence calibration).
  Never redistributed; corpus/ is gitignored.
- removal: delete the ptdb_* files and manifest rows
"""


def sentences_for(number, spec):
    out = []
    for s in spec.split(","):
        if s.startswith("sx+"):
            out.append(f"sx{SX_START[number] + int(s[3:])}")
        else:
            out.append(s)
    return out


def download(url, path):
    if os.path.exists(path):
        return False
    tmp = path + ".part"
    urllib.request.urlretrieve(url, tmp)
    os.replace(tmp, path)
    return True


def convert_reference(f0_path, out_path):
    rows = [line.split() for line in open(f0_path) if line.strip()]
    with open(out_path, "w", newline="", encoding="utf-8") as f:
        f.write("# source: PTDB-TUG laryngograph reference, RAPT (Snack ESPS), 32 ms window, 10 ms hop\n")
        f.write(f"# time: frame i at i*{HOP_S} + {TIME_OFFSET_S} s (offset measured against Praat on the mic, see tools/fetch_ptdb.py)\n")
        f.write("# states: binary (Voiced/Unvoiced; Unvoiced includes silence)\n")
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["time_s", "state", "f0_hz"])
        for i, r in enumerate(rows):
            f0, voiced = float(r[0]), float(r[1]) >= 0.5 and float(r[0]) > 0
            w.writerow([f"{i * HOP_S + TIME_OFFSET_S:.4f}", "Voiced" if voiced else "Unvoiced", f"{f0:.3f}" if voiced else ""])


def read_manifest(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def write_manifest(path, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=MANIFEST_COLUMNS, lineterminator="\n")
        w.writeheader()
        for r in rows:
            w.writerow({k: r.get(k, "") for k in MANIFEST_COLUMNS})


def update_splits(path, speakers):
    existing = {}
    if os.path.exists(path):
        with open(path, newline="", encoding="utf-8") as f:
            existing = {r["speaker_id"]: r["split"] for r in csv.DictReader(f)}
    for s in speakers:
        existing.setdefault(f"ptdb-{s}", "heldout" if s in HELDOUT else "dev")
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["speaker_id", "split"])
        for k in sorted(existing):
            w.writerow([k, existing[k]])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", default="corpus")
    ap.add_argument("--sentences", default="sa1,sa2,sx+0,sx+1",
                    help="per speaker: sa1, sa2, or sx+N (the speaker's Nth sx sentence)")
    args = ap.parse_args()

    for d in ("audio", "reference", "consent"):
        os.makedirs(os.path.join(args.corpus, d), exist_ok=True)
    with open(os.path.join(args.corpus, "consent", "ptdb-tug.md"), "w", encoding="utf-8") as f:
        f.write(CONSENT)
    raw_dir = os.path.join(args.corpus, "raw", "ptdb")
    os.makedirs(raw_dir, exist_ok=True)

    manifest_path = os.path.join(args.corpus, "manifest.csv")
    rows = read_manifest(manifest_path)
    have = {r["file"] for r in rows}
    speakers = [f"{sex}{n:02d}" for sex in "FM" for n in range(1, 11)]
    fetched = 0
    for spk in speakers:
        folder = "FEMALE" if spk[0] == "F" else "MALE"
        for utt in sentences_for(int(spk[1:]), args.sentences):
            stem = f"ptdb_{spk}_{utt}"
            wav = os.path.join(args.corpus, "audio", stem + ".wav")
            f0 = os.path.join(raw_dir, f"ref_{spk}_{utt}.f0")
            ref = os.path.join(args.corpus, "reference", stem + ".csv")
            fetched += download(f"{BASE}/{folder}/MIC/{spk}/mic_{spk}_{utt}.wav", wav)
            download(f"{BASE}/{folder}/REF/{spk}/ref_{spk}_{utt}.f0", f0)
            convert_reference(f0, ref)
            rel = f"audio/{stem}.wav"
            if rel not in have:
                rows.append({
                    "speaker_id": f"ptdb-{spk}", "file": rel, "source": "public", "session_id": f"ptdb-{spk}",
                    "device": "studio headset", "os": "", "condition": "studio-headset", "task": "read",
                    "vowel": "", "target_f0": "", "os_processing_flags": "none", "consent_ref": "consent/ptdb-tug.md",
                    "noise_floor_dbfs": "", "reference": f"reference/{stem}.csv",
                })
                have.add(rel)
            print(f"{stem}", flush=True)

    write_manifest(manifest_path, rows)
    update_splits(os.path.join(args.corpus, "splits.csv"), speakers)
    print(f"{fetched} files downloaded; manifest has {len(rows)} rows")
    return 0


if __name__ == "__main__":
    sys.exit(main())
