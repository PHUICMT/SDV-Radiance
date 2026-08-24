"""Cells that can only draw black: the tile they name is not in the art that was bound.

    python tools/labelops/blackcells.py                    every map, ranked
    python tools/labelops/blackcells.py Forest~0f3236      one map, in detail
    python tools/labelops/blackcells.py --raw              judge only the art the dump recorded

A cell holds (sheet slot, tile index). It draws black when the art behind that slot cannot supply
that index - because no art was recorded for the slot at all, because the art that was is SHORTER
than the map expects, or because it is the WRONG WIDTH. Width is the harsher of the two: an index
only means a position once the column count is known, so a 256-column picture decodes a 25-column
sheet's indices somewhere else entirely and the map draws black from edge to edge. Counting only
"index past the end" called Backwoods clean while 4,119 of its cells were black.

BY DEFAULT THIS JUDGES WHAT THE VIEWER CAN DRAW, NOT WHAT THE DUMP RECORDED. A sheet name has
many pictures in this corpus - spring_outdoorsTileSheet has 112 - and when the recorded one does
not fit, another copy of the same name usually does. The labeller binds a fitting copy rather than
drawing black, so a report that ignores that is a report about a bug that has been fixed, and it
buries the cells that no copy anywhere can answer. Those are the only ones a re-dump can help.

Three verdicts, because they call for three different actions:

    recorded        the art the dump named fits; nothing to do
    another-copy    a different picture of the same name fits; only the record is wrong
    no-art          nothing was captured for the slot - running that pass again is the repair
    width-disagrees the mod's map declares a column count its own art does not have; nothing
                    on this side can fix that, and a re-dump reproduces it exactly
"""
import argparse, base64, collections, json, os, struct, sys

from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twinlabels as labels

HFDIR = labels.HFDIR
CELL_SHEET_STRIDE = 0x100000
TILE = 16

_sizes = {}


def tiles_in(relative):
    """(columns, rows) of a sheet PNG, or None when it cannot be read."""
    if relative not in _sizes:
        try:
            width, height = Image.open(os.path.join(HFDIR, relative)).size
            _sizes[relative] = (width // TILE, height // TILE)
        except Exception:
            _sizes[relative] = None
    return _sizes[relative]


def fits(size, want):
    """Whether this picture can be indexed as the map indexes that slot: the WIDTH, and only it.

    Width is the whole of the question. A tile index means a position only once the column count
    is known, so a 256-column picture decodes a 25-column sheet's indices somewhere else entirely
    and the map draws black edge to edge; counting only "index past the end" once called Backwoods
    clean while 4,119 of its cells were black.

    HEIGHT IS NOT PART OF IT, and treating it as part of it is what made this tool report 8,067
    black cells when the true figure is a fraction of that. The map's tbin declares a sheet size,
    the art on disk is often shorter, and neither fact says a cell is black - only the indices the
    map actually places do. Greenhouse declares 23x41, its art is 23x28, and the highest index it
    ever asks for is 205, which is row 8. Nothing there is black. Demanding the declared height
    condemned all 1,973 of its cells and then rejected the one picture that could draw them.
    """
    if not size:
        return False
    if not want:
        return True
    return size[0] == want[0]


def read_cells(layer):
    cells = layer.get("cells")
    if isinstance(cells, str):
        raw = base64.b64decode(cells)
        return struct.unpack("<%di" % (len(raw) // 4), raw)
    return cells or ()


def examine(entry, index, pool, raw_only=False):
    """One row per hurt slot: what was bound, what fits instead, how many cells go black."""
    document = labels.read_location(entry)
    sheets = document.get("sheets") or []
    arts = document.get("sheetArt") or []
    declared = document.get("sheetWH") or []
    by_name = index.get("artPng") or {}

    reach, chosen = {}, {}
    for slot, name in enumerate(sheets):
        art = arts[slot] if slot < len(arts) else None
        if not art:
            art = by_name.get(name)
        want = declared[slot] if slot < len(declared) else None
        size = tiles_in(art) if art else None
        # Two very different faults hide under "cannot be drawn", and only one is ours to fix.
        # No art at all means the dump never captured the picture, and running that pass again is
        # the repair. A width that disagrees with the map's own declaration is the mod's: it
        # ships one PNG and its map asks for a different shape, and no amount of sweeping changes
        # either file. Stardew Enhanced's Forest.tbin declares zzspring_enhanced at 25x79, copied
        # from the vanilla outdoors entry above it, while the spring_enhanced.png it ships is
        # 19x16 - read straight from the mod's own tbin, not inferred from the dump.
        verdict = ("recorded" if fits(size, want)
                   else "no-art" if not size else "width-disagrees")
        used = art
        if not raw_only:
            # Every picture of this name that can be indexed the way the map indexes the slot,
            # tallest first. Preferring the tallest is not arbitrary: they all share the column
            # count, so more rows can only answer more indices and never a different one. This
            # runs even when the recorded art already fits, because fitting is about width and a
            # taller copy of the same width is strictly better at the bottom of the sheet.
            candidates = sorted((p for p in set(pool.get(name, ())) | {by_name.get(name), art}
                                 if p and fits(tiles_in(p), want)),
                                key=lambda p: (tiles_in(p) or (0, 0))[1], reverse=True)
            if candidates and candidates[0] != art:
                used, size = candidates[0], tiles_in(candidates[0])
                verdict = "by-name" if used == by_name.get(name) else "another-copy"
            elif candidates:
                used, size, verdict = art, tiles_in(art), "recorded"
        chosen[slot] = (name, art, used, verdict, size, want)
        reach[slot] = size[0] * size[1] if (used and fits(size, want)) else 0

    asked = collections.defaultdict(lambda: [0, 0])
    for layer in document.get("layers") or []:
        for value in read_cells(layer):
            if value < 0:
                continue
            slot, tile = divmod(value, CELL_SHEET_STRIDE)
            if slot not in reach or tile < reach[slot]:
                continue
            row = asked[slot]
            row[0] = max(row[0], tile)
            row[1] += 1

    out = []
    for slot, (worst, cells) in asked.items():
        name, art, used, verdict, size, want = chosen[slot]
        out.append(dict(sheet=name, recorded=art, used=used, verdict=verdict, size=size,
                        want=want, worst=worst, cells=cells))
    out.sort(key=lambda row: -row["cells"])
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("map", nargs="?")
    parser.add_argument("--raw", action="store_true",
                        help="judge only the art the dump recorded, with no rescue")
    parser.add_argument("--passes", action="store_true",
                        help="name the passes behind the cells no copy can answer")
    parser.add_argument("--json", help="write the surviving rows to this file")
    arguments = parser.parse_args()
    index = labels.load_index()
    pool = {} if arguments.raw else labels.sheet_versions(index)

    if arguments.map:
        entry = index["locations"].get(arguments.map)
        if not entry:
            sys.exit(f"no map called {arguments.map}")
        rows = examine(entry, index, pool, arguments.raw)
        total = sum(row["cells"] for row in rows)
        print(f"{arguments.map}: {total:,} cell(s) that can only draw black")
        for row in rows:
            size = row["size"] or ("?", "?")
            want = row["want"] or ("?", "?")
            print(f"  {row['cells']:>6} cells  {row['sheet']}   [{row['verdict']}]")
            print(f"          art is {size[0]}x{size[1]} tiles, the map indexes it as "
                  f"{want[0]}x{want[1]}; worst index asked {row['worst']}")
            print(f"          recorded: {row['recorded'] or '(none)'}")
            if row["used"] != row["recorded"]:
                print(f"          bound instead: {row['used'] or '(nothing fits)'}")
        return

    worst_maps, per_sheet, per_verdict, total = [], collections.Counter(), collections.Counter(), 0
    dead_passes = collections.Counter()
    survivors = []
    for key, entry in index["locations"].items():
        rows = examine(entry, index, pool, arguments.raw)
        if not rows:
            continue
        cells = sum(row["cells"] for row in rows)
        total += cells
        worst_maps.append((cells, key, entry.get("name")))
        for row in rows:
            per_sheet[row["sheet"]] += row["cells"]
            per_verdict[row["verdict"]] += row["cells"]
            survivors.append(dict(row, map=key, name=entry.get("name"),
                                  passes=entry.get("from") or []))
            if row["verdict"] == "no-art":
                for pass_name in (entry.get("from") or []):
                    dead_passes[pass_name] += row["cells"]
    worst_maps.sort(reverse=True)
    kind = "as recorded" if arguments.raw else "as the viewer draws it"
    print(f"{total:,} black cell(s) across {len(worst_maps)} map version(s)  ({kind})")
    if not arguments.raw:
        print("  every fitting copy in the corpus was tried, so these are what is really left")
    print()
    print("why:")
    for verdict, cells in per_verdict.most_common():
        print(f"  {cells:>7}  {verdict}")
    print()
    print("worst maps:")
    for cells, key, name in worst_maps[:15]:
        print(f"  {cells:>6}  {key}  ({name})")
    print()
    print("by sheet:")
    for name, cells in per_sheet.most_common(15):
        print(f"  {cells:>6}  {name}")
    if arguments.passes and dead_passes:
        print()
        print("passes behind art no copy can answer:")
        for name, cells in dead_passes.most_common(25):
            print(f"  {cells:>6}  {name}")
    if arguments.json:
        with open(arguments.json, "w", encoding="utf-8") as handle:
            json.dump(survivors, handle, ensure_ascii=False, indent=1)
        print(f"\nwrote {len(survivors)} row(s) to {arguments.json}")


if __name__ == "__main__":
    main()
