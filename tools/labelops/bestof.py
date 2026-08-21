"""Build one repair file from every surviving copy of a sheet's labels, keeping the most complete.

For each pixel, the first source that has anything wins, in the order given. So put the copies
that are known good first and the live file last: a pixel nobody painted since stays whatever the
good copy had, and a pixel only the live file has is work done after that copy and is kept.

Nothing is ever cleared. This can only add, which is what makes it safe to run while something is
still eating labels.

    python tools/labelops/bestof.py spring_town a.json b.json c.json
    python tools/labelops/bestof.py spring_town a.json b.json c.json --write
"""
import argparse, base64, io, json, os, sys, time
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
HF = os.path.expanduser(r"~\Documents\HF-Studio")
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]

parser = argparse.ArgumentParser()
parser.add_argument("sheet")
parser.add_argument("sources", nargs="+", help="label files, best first")
parser.add_argument("--write", action="store_true")
parser.add_argument("--out", default=None)
args = parser.parse_args()


def tiles_of(path):
    with io.open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    body = document.get("sheets", {}).get(args.sheet) if "sheets" in document else document
    return (body or {}).get("tiles") or {}


def paint(tiles):
    counter = Counter()
    for blob in tiles.values():
        for b in base64.b64decode(blob):
            if b:
                counter[CLASSES[b - 1] if 0 < b <= len(CLASSES) else str(b)] += 1
    return counter


sources = []
for path in args.sources:
    full = path if os.path.isabs(path) else os.path.join(HF, path)
    if not os.path.exists(full):
        print(f"missing, skipped: {path}")
        continue
    tiles = tiles_of(full)
    sources.append((full, tiles))
    when = time.strftime("%m-%d %H:%M:%S", time.localtime(os.path.getmtime(full)))
    total = sum(paint(tiles).values())
    print(f"{when}  {len(tiles):>4} tiles  {total:>8,} px  {os.path.relpath(full, HF)}")
assert sources, "no readable source"

merged = {}
credit = Counter()
for index in sorted({i for _, t in sources for i in t}, key=int):
    out = bytearray(256)
    for rank, (path, tiles) in enumerate(sources):
        if index not in tiles:
            continue
        data = base64.b64decode(tiles[index])
        for p in range(256):
            if out[p] == 0 and data[p]:
                out[p] = data[p]
                credit[rank] += 1
    if any(out):
        merged[index] = base64.b64encode(bytes(out)).decode("ascii")

live = sources[-1][1]
print(f"\n{'class':16}{'live now':>10}{'merged':>10}{'change':>9}")
now, after = paint(live), paint(merged)
for name in sorted(set(now) | set(after), key=lambda k: -after.get(k, 0)):
    d = after.get(name, 0) - now.get(name, 0)
    print(f"{name:16}{now.get(name, 0):>10,}{after.get(name, 0):>10,}"
          f"{('+' + format(d, ',')) if d > 0 else format(d, ','):>9}")
print(f"\n{len(merged)} tiles, {sum(after.values()):,} px; where each pixel came from:")
for rank, (path, _) in enumerate(sources):
    print(f"   {credit[rank]:>8,}  {os.path.relpath(path, HF)}")

out = args.out or os.path.join(HF, f"{args.sheet}-restore.json")
if not args.write:
    print(f"\nnothing written; pass --write to save to\n  {out}")
else:
    with io.open(out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"sheet": args.sheet, "tiles": merged}, handle, ensure_ascii=False)
        handle.write("\n")
    print(f"\nwritten: {out}\nImport that in HF Studio. It merges per tile and only ever adds.")
