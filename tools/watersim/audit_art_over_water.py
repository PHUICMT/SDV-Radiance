"""Offline audit of the one rule this subsystem is held to: nothing drawn on top of water
may receive the water effect.

The mod's only per-pixel sources of truth for "this is art, not water" are the labels and
the opacity of overlay-layer art. So any tile that the GAME calls water, that has art on it,
and whose art is unlabelled, is a place the effect can land on something solid - Willy's boat
at the fish shop and the BoatTunnel boat are both this shape.

Reads the map dump (Documents/HF-Studio/maps.json, the same file the labeler renders from)
and the shipped label set, so it needs neither the game running nor the location reachable -
BoatTunnel and Ginger Island are behind progression this save has not reached.

    python tools/watersim/audit_art_over_water.py [--layer Buildings] [--top 25]
"""
import argparse
import base64
import collections
import json
import os
import re
import struct

MAPS = os.path.expanduser("~/Documents/HF-Studio/maps.json")
LABELS = os.path.join(os.path.dirname(__file__), "..", "..", "labels", "water-labels.json")
# Overlay layers carve by opacity; Back never does (it IS the ground/water plane), so
# unlabelled Back art over a water tile is the case with no defence at all.
LAYERS = ("Buildings", "Front", "AlwaysFront", "Back")


def norm(sheet):
    """Label keys are bare sheet names; map sheets arrive as paths with an extension."""
    s = sheet.replace("\\", "/").lower()
    s = re.sub(r"^.*/", "", s)
    return re.sub(r"\.(png|xnb)$", "", s)


def b64(s):
    return base64.b64decode(s + "=" * (-len(s) % 4))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--layer", help="only report this layer")
    ap.add_argument("--top", type=int, default=25)
    ap.add_argument("--loc", help="only this location")
    args = ap.parse_args()

    maps = json.load(open(MAPS, encoding="utf-8"))
    labels = json.load(open(LABELS, encoding="utf-8"))["sheets"]
    painted = {norm(k): {int(t) for t in v.get("tiles", {})} for k, v in labels.items()}
    layers = (args.layer,) if args.layer else LAYERS

    rows = []
    for name, loc in maps["locations"].items():
        if args.loc and args.loc.lower() not in name.lower():
            continue
        if not loc.get("wgrid"):
            continue
        lay = {e["id"]: e for e in loc["layers"]}
        if "Back" not in lay:
            continue
        w, h = lay["Back"]["w"], lay["Back"]["h"]
        grid = b64(loc["wgrid"])
        sheets = loc["sheets"]
        if len(grid) * 8 < w * h:
            continue

        unlabelled = collections.Counter()
        for y in range(h):
            for x in range(w):
                i = y * w + x
                if not ((grid[i >> 3] >> (i & 7)) & 1):   # LSB-first, as MapDump packs it
                    continue
                for lid in layers:
                    e = lay.get(lid)
                    if not e:
                        continue
                    raw = b64(e["cells"])
                    per = len(raw) // (w * h)
                    cell = raw[i * per:i * per + per]
                    if all(b == 0xFF for b in cell):      # -1 = no tile here
                        continue
                    # MapDump packs int32 = sheetIndex * 0x100000 + tileIndex, -1 = empty.
                    v = struct.unpack("<i", cell)[0]
                    if v < 0:
                        continue
                    sheet, tile = v >> 20, v & 0xFFFFF
                    if sheet >= len(sheets):
                        continue
                    key = norm(sheets[sheet])
                    if tile not in painted.get(key, ()):
                        unlabelled[(key, lid)] += 1
        if unlabelled:
            rows.append((sum(unlabelled.values()), name, unlabelled))

    rows.sort(reverse=True)
    print(f"{'tiles':>7}  location                            worst sheets")
    for total, name, unlabelled in rows[:args.top]:
        worst = ", ".join(f"{s}/{l}:{n}" for (s, l), n in unlabelled.most_common(3))
        print(f"{total:7}  {name:34}  {worst}")
    print(f"\nlocations affected: {len(rows)}   tiles total: {sum(r[0] for r in rows)}")

    per_sheet = collections.Counter()
    for _, _, unlabelled in rows:
        for (sheet, lid), n in unlabelled.items():
            per_sheet[(sheet, lid)] += n
    print("\nby sheet + layer (paint these to clear the most at once):")
    for (sheet, lid), n in per_sheet.most_common(args.top):
        print(f"{n:7}  {sheet}  [{lid}]")


if __name__ == "__main__":
    main()
