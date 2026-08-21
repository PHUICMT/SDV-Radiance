"""Sweep every surviving copy of every sheet's labels and put back what is missing.

Purely additive, and the live files come FIRST: a pixel that holds something now keeps what it
holds, and only a pixel that is EMPTY can be filled from an older copy. So this recovers work that
was erased and cannot undo work that was repainted, which is the only version of this that is safe
to run without reading every sheet first.

A repaint is a different problem and needs restoreclass.py, which is told exactly which class
covered which.

    python tools/labelops/recoverall.py             say what could come back
    python tools/labelops/recoverall.py --write     write one import file per sheet that gained
"""
import argparse, base64, io, json, os, sys, time
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
HF = os.path.expanduser(r"~\Documents\HF-Studio")
REPO_PACK = r"e:\Games\GamesMods\DevStardew\SDV-Radiance\labels\water-labels.json"
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]

parser = argparse.ArgumentParser()
parser.add_argument("--write", action="store_true")
parser.add_argument("--out", default=None, help="folder for the per-sheet import files")
args = parser.parse_args()


def folder_source(folder, label):
    """Every sheet in a folder of <name>.labels.json files."""
    got = {}
    if not os.path.isdir(folder):
        return label, got
    for name in os.listdir(folder):
        if not name.endswith(".labels.json"):
            continue
        try:
            got[name[: -len(".labels.json")]] = json.load(
                io.open(os.path.join(folder, name), encoding="utf-8")).get("tiles") or {}
        except Exception:
            pass
    return label, got


def pack_source(path, label):
    got = {}
    if not os.path.exists(path):
        return label, got
    try:
        doc = json.load(io.open(path, encoding="utf-8"))
    except Exception:
        return label, got
    for name, body in (doc.get("sheets") or {}).items():
        if isinstance(body, dict) and body.get("tiles"):
            got[name] = body["tiles"]
    return label, got


# Live first. Everything after it can only fill gaps.
SOURCES = [folder_source(HF, "live")]
for name in sorted(os.listdir(HF)):
    if name.startswith("_labels-backup-") and os.path.isdir(os.path.join(HF, name)):
        for sub in ("top-level", "labels", ""):
            SOURCES.append(folder_source(os.path.join(HF, name, sub), f"{name}/{sub or '.'}"))
SOURCES.append(folder_source(os.path.join(HF, "labels"), "labels/"))
SOURCES.append(pack_source(REPO_PACK, "shipped pack (repo)"))
SOURCES = [(label, data) for label, data in SOURCES if data]

print("sources, best first:")
for label, data in SOURCES:
    total = sum(sum(1 for b in base64.b64decode(v) if b) for t in data.values() for v in t.values())
    print(f"   {len(data):>4} sheets  {total:>10,} px  {label}")

live = dict(SOURCES[0][1])
gains = {}
credit = Counter()
for sheet in sorted({s for _, d in SOURCES for s in d}):
    merged = {}
    for rank, (label, data) in enumerate(SOURCES):
        for index, blob in (data.get(sheet) or {}).items():
            raw = base64.b64decode(blob)
            out = merged.get(index)
            if out is None:
                out = merged[index] = bytearray(256)
            for p in range(256):
                if out[p] == 0 and raw[p]:
                    out[p] = raw[p]
                    if rank:
                        credit[label] += 1
    now = live.get(sheet) or {}
    before = sum(1 for v in now.values() for b in base64.b64decode(v) if b)
    after = sum(1 for v in merged.values() for b in v if b)
    if after > before:
        gains[sheet] = (before, after, {i: base64.b64encode(bytes(v)).decode("ascii")
                                        for i, v in merged.items() if any(v)})

print(f"\n{len(gains)} sheet(s) can get something back:\n")
print(f"{'sheet':40}{'now':>10}{'recovered':>11}{'gain':>9}")
for sheet, (before, after, _) in sorted(gains.items(), key=lambda kv: kv[1][0] - kv[1][1]):
    print(f"{sheet:40}{before:>10,}{after:>11,}{'+' + format(after - before, ','):>9}")
print(f"\nwhere the recovered pixels came from:")
for label, n in credit.most_common():
    print(f"   {n:>9,}  {label}")

out_dir = args.out or os.path.join(HF, "_recover-" + time.strftime("%Y%m%d-%H%M%S"))
if not args.write:
    print(f"\nnothing written; pass --write to save one import file per sheet into\n  {out_dir}")
else:
    os.makedirs(out_dir, exist_ok=True)
    for sheet, (_, _, tiles) in gains.items():
        with io.open(os.path.join(out_dir, sheet + ".json"), "w", encoding="utf-8", newline="\n") as h:
            json.dump({"sheet": sheet, "tiles": tiles}, h, ensure_ascii=False)
            h.write("\n")
    print(f"\nwritten {len(gains)} file(s) to\n  {out_dir}\nImport them in HF Studio; each only ever adds.")
