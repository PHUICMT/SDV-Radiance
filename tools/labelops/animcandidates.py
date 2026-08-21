"""Animated tiles above Back that nobody has painted yet, ranked by how much they cover.

Water that MOVES - a waterfall face, a fountain jet, a mine's bubbling pool - is art the map
lays on a layer above Back, and the mod has no way to know it is moving water except from a
label. An unpainted one falls through to the still water underneath, which is why a waterfall
starts on a straight tile row instead of at the lip of the fall.

Not every animated tile is water: braziers, flags and mine lamps are in here too. This is a
candidate list to work down in the labeller, not a verdict.

    python tools/labelops/animcandidates.py

How many DISTINCT sheet tiles make up every waterfall lip in the corpus?

A label is keyed by (sheet, tile index), so painting one covers every map that places it. The
question that decides whether the seam is an afternoon of painting or a code problem is not how
many map tiles look wrong, it is how many distinct pieces of ART they are.

A candidate is a tile that is:
  * part of an animation group (a waterfall face animates; still water on Back does not), and
  * placed on a layer ABOVE Back, and
  * has no label painted on it yet.
"""
import base64, io, json, os, struct, sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")
HF = os.path.expanduser(r"~\Documents\HF-Studio")

dump = json.load(io.open(os.path.join(HF, "maps.json"), encoding="utf-8"))
locations = dump["locations"]

painted = {}


def labelled_tiles(sheet):
    """Tile indices on this sheet that carry ANY painted pixel."""
    if sheet in painted:
        return painted[sheet]
    got = set()
    for path in (os.path.join(HF, sheet + ".labels.json"), os.path.join(HF, "labels", sheet + ".json")):
        try:
            tiles = json.load(io.open(path, encoding="utf-8")).get("tiles") or {}
        except OSError:
            continue
        for index, blob in tiles.items():
            if any(base64.b64decode(blob)):
                got.add(int(index))
    painted[sheet] = got
    return got


ABOVE_BACK = ("Front", "AlwaysFront", "Front2", "AlwaysFront2", "Buildings")
candidates = Counter()          # (sheet, index) -> how many map versions place it
places = defaultdict(set)
scanned = 0

for verkey, entry in locations.items():
    path = entry.get("file")
    if not path:
        continue
    try:
        sub = json.load(io.open(os.path.join(HF, path), encoding="utf-8"))
    except OSError:
        continue
    scanned += 1
    sheets = entry.get("sheets") or []
    for layer in sub.get("layers") or []:
        if not layer["id"].startswith(ABOVE_BACK):
            continue
        anim = set(layer.get("anim") or [])
        if not anim:
            continue
        raw = base64.b64decode(layer["cells"])
        cells = struct.unpack("<%di" % (len(raw) // 4), raw)
        for i in anim:
            if i >= len(cells):
                continue
            value = cells[i]
            if value < 0:
                continue
            si, index = value >> 20, value & 0xFFFFF
            if si >= len(sheets):
                continue
            sheet = sheets[si]
            if index in labelled_tiles(sheet):
                continue
            candidates[(sheet, index)] += 1
            places[(sheet, index)].add(entry.get("name", verkey))

print(f"scanned {scanned:,} map version(s)")
print(f"\ndistinct ANIMATED, UNLABELLED sheet tiles placed above Back: {len(candidates):,}")
print(f"they account for {sum(candidates.values()):,} placements\n")

by_sheet = Counter()
for (sheet, _), n in candidates.items():
    by_sheet[sheet] += 1
print("top sheets by how many distinct tiles are involved:")
for sheet, n in by_sheet.most_common(15):
    hit = sum(v for (s, _), v in candidates.items() if s == sheet)
    print(f"  {n:4d} tiles   {hit:6,} placements   {sheet}")

print(f"\nsheets involved at all: {len(by_sheet):,}")
covered = sum(sorted(candidates.values(), reverse=True)[:200])
print(f"the 200 most-placed tiles cover {covered:,} of {sum(candidates.values()):,} placements "
      f"({100.0 * covered / max(1, sum(candidates.values())):.0f}%)")
