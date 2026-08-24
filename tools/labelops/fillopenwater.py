"""Label the open water the GAME already says is water, and nothing else.

    python tools/labelops/fillopenwater.py           measure, check the rule, write nothing
    python tools/labelops/fillopenwater.py --write   write the import file for HF Studio

The colour guess that was taken out of this project marked 15,101 tiles as water because their
pixels leaned blue, and blue flowers went with them. This is not that. The dump records `wgrid`,
which is the game's OWN answer for whether a map cell is water, so the question here is never
"does this look wet" but "did the game say so", and the only judgement left is which PIXELS of a
tile the answer covers.

THE RULE, AND HOW IT WAS ARRIVED AT. Every candidate must be wet EVERY time it is placed - not
merely somewhere - and its art must be opaque to every corner. Both halves were measured against
tiles a person has already painted rather than assumed:

    wet every time it is placed      88.5% were painted as water on all 256 pixels
    wet only sometimes               52.2%
    never wet in any map              23.0%

so "wet every time" separates and the others do not. A neighbour test was tried too - refuse a
tile that touches a dry cell, reasoning that shorelines are half sand - and it was DROPPED: tiles
touching dry ground were painted full 89.0% of the time, indistinguishable from open water, since
the shoreline lives on the land tile rather than the water one. It cost 750 good tiles for nothing
and is not here.

What is left of the error is art that stands out of water - a rock, a post, a stump - which the
game still calls a wet cell. Those carry transparent pixels where the water shows past them, so
opacity is the test, and this file prints how well it holds before it writes anything.

A HAND ANSWER WINS, WHICHEVER WAY IT POINTS. The first run of this file only stepped around tiles
already labelled as liquid, so a tile a person had deliberately marked as ground was fair game - and
eight of them were overwritten, among them two tree canopies that a map had placed on the back layer
over a pond. `wgrid` answers for the CELL, not for the art standing in it, so where a person has
already ruled on the art their answer is the better evidence and is left alone.

The output is an IMPORT FILE rather than a write into anybody's store: it can be read, imported, or
thrown away, and importing merges rather than replacing.
"""
import argparse, base64, collections, io, json, os, sys

import numpy

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twinlabels as labels
import blackcells as black
import artfingerprints as prints

LIQUID = {1, 9, 10, 11}          # water, ice, flowing, lava
WATER = 1
CELL_SHEET_STRIDE = 0x100000
OUT = os.path.join(labels.LABELDIR, "!open-water-fill.json")
WATER_LAYERS = ("back", "back2")


def wet_counts(index):
    """(sheet, tile) -> [times placed, times wet, times wet ON THE BASE PICTURE].

    The third number is the one that decides. Wetness is read from maps, and a map may be drawing
    a mod's copy of the sheet - so a cell counted wet says "the picture THAT MAP had is water
    there". Writing that into the shared store, which belongs to the base picture, would be
    carrying evidence from one picture over to another. It is only the same claim when the map was
    drawing the base picture itself.
    """
    counts = collections.defaultdict(lambda: [0, 0, 0])
    by_name = index.get("artPng") or {}
    for entry in index["locations"].values():
        document = labels.read_location(entry)
        names = document.get("sheets") or []
        arts = document.get("sheetArt") or []
        grid = document.get("wgrid")
        if not grid:
            continue
        raw = base64.b64decode(grid)
        for layer in document.get("layers") or []:
            if (layer.get("id") or "").lower() not in WATER_LAYERS:
                continue
            for position, value in enumerate(black.read_cells(layer)):
                if value < 0:
                    continue
                slot, tile = divmod(value, CELL_SHEET_STRIDE)
                if slot >= len(names):
                    continue
                name = names[slot]
                row = counts[(name, tile)]
                row[0] += 1
                byte = position >> 3
                if not (byte < len(raw) and (raw[byte] >> (position & 7)) & 1):
                    continue
                row[1] += 1
                art = arts[slot] if slot < len(arts) else None
                if art is None or art == by_name.get(name):
                    row[2] += 1
    return counts


def painted_now():
    """sheet -> tile -> the 256 class bytes somebody has already painted."""
    out = {}
    for name, path in labels.label_files().items():
        document = labels.read_labels(path)
        if document and isinstance(document.get("tiles"), dict):
            out[name] = {int(key): base64.b64decode(blob)
                         for key, blob in document["tiles"].items()}
    return out


def opaque(block):
    """True when the art covers the tile to every corner, so water can show nowhere past it."""
    return bool(block is not None and (block[:, :, 3] == 255).all())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    index = labels.load_index()
    by_name = index.get("artPng") or {}
    counts = wet_counts(index)
    painted = painted_now()

    # ---- check the rule against the hand that painted, before trusting it with anything --------
    checked = collections.Counter()
    for (name, tile), (placed, wet, on_base) in counts.items():
        marks = painted.get(name, {}).get(tile)
        if marks is None or wet != placed or not wet:
            continue
        image = prints.pixels(by_name.get(name)) if by_name.get(name) else None
        block = prints.block_of(image, tile) if image is not None else None
        if block is None:
            continue
        group = "opaque" if opaque(block) else "has transparency"
        liquid = sum(1 for one in marks if one in LIQUID)
        checked[(group, "all 256" if liquid == 256 else
                 "none" if liquid == 0 else "partial")] += 1
    print("the rule, measured against tiles a person has already painted:")
    for group in ("opaque", "has transparency"):
        total = sum(count for (kind, _), count in checked.items() if kind == group)
        if not total:
            continue
        full = checked[(group, "all 256")]
        print(f"  wet every time and {group:<17} {total:>6} tile(s), "
              f"{100 * full / total:>5.1f}% painted as water on all 256 pixels")
        for shape in ("partial", "none"):
            if checked[(group, shape)]:
                print(f"      {shape:<8} {checked[(group, shape)]:>6}")

    # ---- what would be written -----------------------------------------------------------------
    fill, review, hearsay = collections.Counter(), collections.Counter(), collections.Counter()
    answered = collections.Counter()
    tiles_out = collections.defaultdict(dict)
    blob = base64.b64encode(bytes([WATER] * 256)).decode()
    for (name, tile), (placed, wet, on_base) in sorted(counts.items()):
        if not wet or wet != placed:
            continue
        # The shared store belongs to the base picture, so the evidence has to come from a map
        # that was drawing the base picture. Wetness seen only through a mod's copy describes
        # that copy, and a variant is where that answer belongs.
        if not on_base:
            hearsay[name] += 1
            continue
        if tile in painted.get(name, {}):
            answered[name] += 1
            continue                      # a person answered this one, and either answer wins
        art = by_name.get(name)
        image = prints.pixels(art) if art else None
        block = prints.block_of(image, tile) if image is not None else None
        if block is None:
            continue
        if not opaque(block):
            review[name] += 1
            continue
        fill[name] += 1
        tiles_out[name][str(tile)] = blob

    print()
    print(f"{sum(fill.values()):,} tile(s) would be filled as water")
    print(f"{sum(review.values()):,} left for a person: wet, but the art does not cover the tile")
    print(f"{sum(hearsay.values()):,} skipped: only ever seen wet through a mod's copy of the sheet")
    print(f"{sum(answered.values()):,} left alone: a person had already answered them")
    print()
    for name, count in fill.most_common(12):
        print(f"  {count:>6}  {name}")

    if not arguments.write:
        print("\npass --write to create the import file")
        return
    payload = {"//": "Open water the game itself marks wet, on tiles whose art covers every pixel. "
                     "Written by tools/labelops/fillopenwater.py. Import merges, so nothing "
                     "already painted is replaced.",
               "format": 1, "classes": labels.CLASSES,
               "sheets": {name: {"tiles": tiles} for name, tiles in sorted(tiles_out.items())}}
    with io.open(OUT, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False)
    print(f"\nwrote {OUT}  ({os.path.getsize(OUT) / 1024:.0f} KB)")
    print("in HF Studio: Import -> pick this file")


if __name__ == "__main__":
    main()
