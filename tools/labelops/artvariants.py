"""Ship the labels painted for ONE picture, widened to every repaint of that same picture.

    python tools/labelops/artvariants.py           measure and report, write nothing
    python tools/labelops/artvariants.py --write   write labels/art-variants.json

HF Studio holds a label apart when a picture DRAWS a tile differently from the base picture of its
name: the fish shack a pack put where Willy's shop was gets its own answer, and painting it leaves
the base alone. Those answers are keyed by the fingerprint of the art they were painted on, and
the page writes them to art-variants.json in the Live JSON folder with that one fingerprint each,
because a page has one picture in hand and this has the whole corpus on disk.

WHAT THIS ADDS is the same thing artfingerprints.py adds to the shared labels: a variant painted
on one picture is right for every REPAINT of that picture too. Ridgeside's shrine repainted by a
recolour pack is the same drawing under a new palette, and the person who labelled it once should
not be asked to label it again per palette. Each entry's `art` list grows from the one fingerprint
the page knew to every fingerprint in the corpus whose tile has the same outline and the same
four-level shading - twinlabels' key, which is what a palette swap keeps.

The mod takes the first entry whose art list contains the live fingerprint, so a wider list is a
variant that reaches more installs and never a wrong answer: a fingerprint only enters the list
when the drawing matched.

Two things this drops rather than ships, because the mod would drop them anyway and silence at
this end is cheaper to debug than silence at that one: a label that is not exactly 256 bytes, and
an entry whose fingerprint matches no art anywhere in the corpus.
"""
import argparse, collections, io, json, os, sys

import numpy
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twinlabels as labels
import artfingerprints as prints

REPO = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(REPO, "labels", "art-variants.json")
SOURCE = os.path.join(labels.LABELDIR, "art-variants.json")
TILE = 16


def painted_variants():
    """sheet -> tile -> [entry], as HF Studio wrote it."""
    if not os.path.exists(SOURCE):
        sys.exit(f"no art-variants.json in {labels.LABELDIR}\n"
                 f"connect the Live JSON folder in HF Studio and paint on a picture that redraws "
                 f"part of its sheet; the page writes the file on every sync.")
    with io.open(SOURCE, encoding="utf-8") as handle:
        return (json.load(handle) or {}).get("sheets") or {}


def art_by_fingerprint(index, name, tiles):
    """tile -> {fingerprint: shading key}, over every picture of that sheet name.

    Every picture is decoded ONCE and asked about all the tiles at once. Doing it per tile meant
    re-decoding the same PNG for each of them - 216 tiles of night_market_tilesheet_objects
    against a few dozen pictures is thousands of decodes of files that had not changed, and the
    run never finished.
    """
    out = {tile: {} for tile in tiles}
    for path in sorted(labels.sheet_versions(index).get(name, ())):
        image = prints.pixels(path)
        if image is None:
            continue
        for tile in tiles:
            block = prints.block_of(image, tile)
            if block is None:
                continue
            key = labels.shading_key(block)
            if key is None:
                continue
            for fingerprint in prints.both_alpha_readings(block):
                out[tile][fingerprint] = key
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    index = labels.load_index()
    sheets = painted_variants()
    written, widened, dropped_label, dropped_art, dropped_name = {}, 0, 0, 0, 0
    pictures = labels.sheet_versions(index)
    rows = []
    for name, by_tile in sorted(sheets.items()):
        out_tiles = {}
        every = art_by_fingerprint(index, name, sorted(int(k) for k in by_tile))
        for tile, entries in sorted(by_tile.items(), key=lambda kv: int(kv[0])):
            known = every.get(int(tile), {})
            kept = []
            for entry in entries:
                label = entry.get("label") or ""
                import base64
                try:
                    if len(base64.b64decode(label)) != 256:
                        dropped_label += 1
                        continue
                except Exception:
                    dropped_label += 1
                    continue
                art = [a for a in (entry.get("art") or []) if isinstance(a, str)]
                # Every fingerprint whose tile is the same DRAWING as the one this was painted on.
                wanted = {known[a] for a in art if a in known}
                if not wanted:
                    # No picture of THIS NAME can produce that fingerprint. Two very different
                    # reasons, and only one of them is worth shipping.
                    #
                    # The name has pictures here and none of them match: the entry is under the
                    # wrong name. The page fans a variant out to every season that shares the
                    # store, because the mod looks sheets up by name - but a fingerprint belongs
                    # to one picture, and this dump is a SPRING dump, so summer_beach has three
                    # pictures to spring_beach's twenty-nine and fall_beach has one. Those copies
                    # can never match anything and were three quarters of the file.
                    #
                    # The name has no pictures here at all: nothing has been shown, only that this
                    # corpus has not met that art. The entry ships as painted - it is the one
                    # claim in the file somebody made while looking at the picture.
                    if pictures.get(name):
                        dropped_name += 1
                        continue
                    dropped_art += 1
                    kept.append({"source": entry.get("source") or "HF Studio",
                                 "art": sorted(set(art)), "label": label})
                    continue
                grown = sorted({a for a, key in known.items() if key in wanted} | set(art))
                widened += len(grown) - len(set(art))
                kept.append({"source": entry.get("source") or "HF Studio",
                             "art": grown, "label": label})
            if kept:
                out_tiles[str(int(tile))] = kept
        if out_tiles:
            written[name] = out_tiles
            rows.append((sum(len(v) for v in out_tiles.values()), len(out_tiles), name))

    rows.sort(reverse=True)
    tiles = sum(len(v) for v in written.values())
    entries = sum(len(e) for v in written.values() for e in v.values())
    print(f"{tiles:,} tile(s) held apart across {len(written)} sheet(s), {entries:,} label(s)")
    print(f"  fingerprints added by matching the drawing: {widened:,}")
    if dropped_label:
        print(f"  dropped, not 256 bytes: {dropped_label}")
    if dropped_name:
        print(f"  dropped, no picture of that NAME can produce it: {dropped_name}")
    if dropped_art:
        print(f"  kept as painted, that name has no art here to judge by: {dropped_art}")
    print()
    for count, tile_count, name in rows[:15]:
        print(f"  {tile_count:>5} tile(s)  {count:>5} label(s)  {name}")

    if not arguments.write:
        print("\npass --write to update labels/art-variants.json")
        return
    with io.open(OUT, "w", encoding="utf-8") as handle:
        json.dump({"format": 1,
                   "//": "Labels painted for a PICTURE rather than a sheet name, each tied to "
                         "every fingerprint of that same drawing. HF Studio paints them; "
                         "tools/labelops/artvariants.py widens and ships them.",
                   "sheets": written}, handle, ensure_ascii=False, separators=(",", ":"))
    print(f"\nwrote {OUT}  ({os.path.getsize(OUT) / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
