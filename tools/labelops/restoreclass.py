"""Put one deleted CLASS back into a sheet's labels, without touching anything painted since.

A whole-file restore is the wrong tool for an accidental erase: the backup is older than the work
that came after it, and HF Studio's import replaces a tile's 256 bytes wholesale, so restoring 151
tiles would take back every stroke made on them today.

This builds a repair file instead. Per pixel: keep what is there now, and where it is EMPTY and the
backup had the class being restored, put that class back. Anything repainted since keeps its new
value, because a pixel that now holds something is not a pixel that was erased.

    python tools/labelops/restoreclass.py spring_town reflect_floor <backup.json>
    python tools/labelops/restoreclass.py spring_town reflect_floor <backup.json> --write
"""
import argparse, base64, io, json, os, sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
HF = os.path.expanduser(r"~\Documents\HF-Studio")
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]

parser = argparse.ArgumentParser()
parser.add_argument("sheet")
parser.add_argument("klass", help="the class to put back, by name")
parser.add_argument("backup", help="a copy of that sheet's labels from before the erase")
parser.add_argument("--write", action="store_true", help="write the repair file")
parser.add_argument("--over", default=None,
                    help="also restore where the pixel now holds THIS class, for when the erase was "
                         "a stroke of something else across it rather than a rub-out")
parser.add_argument("--out", default=None)
args = parser.parse_args()

assert args.klass in CLASSES, f"unknown class {args.klass}; one of {CLASSES}"
WANT = CLASSES.index(args.klass) + 1
assert not args.over or args.over in CLASSES, f"unknown class {args.over}"
OVER = CLASSES.index(args.over) + 1 if args.over else 0


def tiles_of(path):
    with io.open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    body = document.get("sheets", {}).get(args.sheet) if "sheets" in document else document
    return document, (body or {}).get("tiles") or {}

current_doc, current = tiles_of(os.path.join(HF, args.sheet + ".labels.json"))
_, backup = tiles_of(args.backup)

repaired = {}
restored = 0
kept_repaint = 0
touched_tiles = 0

for index in set(current) | set(backup):
    now = bytearray(base64.b64decode(current[index])) if index in current else bytearray(256)
    was = base64.b64decode(backup[index]) if index in backup else bytes(256)
    changed = False
    for p in range(256):
        if was[p] != WANT:
            continue
        if now[p] == 0 or (OVER and now[p] == OVER):
            now[p] = WANT
            restored += 1
            changed = True
        elif now[p] != WANT:
            kept_repaint += 1
    if changed:
        touched_tiles += 1
    if any(now):
        repaired[index] = base64.b64encode(bytes(now)).decode("ascii")

before = Counter()
after = Counter()
for table, counter in ((current, before), (repaired, after)):
    for blob in table.values():
        for b in base64.b64decode(blob):
            if b:
                counter[CLASSES[b - 1] if 0 < b <= len(CLASSES) else b] += 1

print(f"{args.sheet}: restoring {args.klass}\n")
print(f"{'class':16}{'now':>9}{'repaired':>10}{'change':>9}")
for name in sorted(set(before) | set(after), key=lambda k: -after.get(k, 0)):
    d = after.get(name, 0) - before.get(name, 0)
    print(f"{name:16}{before.get(name, 0):>9,}{after.get(name, 0):>10,}{('+' + format(d, ',')) if d > 0 else format(d, ','):>9}")
print(f"\n{restored:,} pixel(s) put back across {touched_tiles} tile(s)")
if kept_repaint:
    print(f"{kept_repaint:,} pixel(s) the backup had as {args.klass} now hold something else, and were LEFT ALONE")

out = args.out or os.path.join(HF, f"{args.sheet}-restore-{args.klass}.json")
if not args.write:
    print(f"\nnothing written; pass --write to save the repair file to\n  {out}")
else:
    with io.open(out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"sheet": args.sheet, "tiles": repaired}, handle, ensure_ascii=False)
        handle.write("\n")
    print(f"\nwritten: {out}")
    print("In HF Studio press Import and pick that file. It merges per tile, so nothing else moves.")
