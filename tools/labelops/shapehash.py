"""Ask whether a label can survive a recolour: does a structure-only hash separate the right things?

The exact fingerprint the mod ships today asks "is this the same picture, byte for byte". That is
correct and it is also strict: a recolour repaints a window without moving it, so the label stays
right while the hash goes wrong. Measured on this machine, one recolour changed the art under 569
glass tiles that were all still correctly labelled.

A structure-only hash would accept those, and would also collapse Elle's four palettes into one
set of labels rather than four. The question is whether it can still tell a REDESIGN apart from a
recolour, and that is not a thing to assume. This scores it against three pairs whose answer we
already know:

    same shape, different colour   (vanilla vs a recolour)      -> must MATCH
    same shape, different colour   (Elle palette A vs B)        -> must MATCH
    different shape                (vanilla vs Elle)            -> must DIFFER

    python shapehash.py <sheetA.png> <sheetB.png> [--tiles a,b,c] [--expect match|differ]

Prints, per candidate hash, how many tiles agree and how many do not, so the three runs can be
read side by side. Nothing here touches the mod: this is offline arithmetic on two PNGs.
"""
import argparse, sys

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

TILE = 16


def tiles_of(path):
    """Every 16x16 tile of a sheet as (index, RGBA float array), row major."""
    image = Image.open(path).convert("RGBA")
    width, height = image.size
    pixels = np.asarray(image).astype(np.float32)
    per_row = width // TILE
    out = {}
    for index in range(per_row * (height // TILE)):
        x, y = (index % per_row) * TILE, (index // per_row) * TILE
        out[index] = pixels[y:y + TILE, x:x + TILE, :]
    return out, per_row


def luminance(tile):
    """Rec. 709 grey, with transparent pixels pinned to one value so alpha shape still counts."""
    grey = tile[:, :, 0] * 0.2126 + tile[:, :, 1] * 0.7152 + tile[:, :, 2] * 0.0722
    return np.where(tile[:, :, 3] < 8, -1.0, grey)


def hash_exact(tile):
    """What the mod ships today: every channel, every pixel."""
    return tile.astype(np.uint8).tobytes()


def hash_alpha(tile):
    """Opacity only. Included because it is the obvious idea and it is the wrong one: a wall and
    a different wall are both fully opaque, which is exactly the case that has to separate."""
    return (tile[:, :, 3] >= 8).tobytes()


def hash_edges(tile):
    """Which way the picture gets brighter, per pixel, horizontally and vertically.

    A recolour moves every value together and leaves the direction of each step alone. A redesign
    moves the steps. Transparent pixels carry -1 so a silhouette change also shows up here.
    """
    grey = luminance(tile)
    horizontal = np.sign(np.diff(grey, axis=1)).astype(np.int8)
    vertical = np.sign(np.diff(grey, axis=0)).astype(np.int8)
    return horizontal.tobytes() + vertical.tobytes()


def hash_edges_deadband(tile, deadband=6.0):
    """As above, but a step smaller than the deadband counts as flat.

    Without it, two pixels a hair apart in the original can land either side of zero once a
    recolour has nudged them, and the hash flips on noise rather than on structure.
    """
    grey = luminance(tile)
    horizontal = np.diff(grey, axis=1)
    vertical = np.diff(grey, axis=0)
    quantise = lambda d: np.where(np.abs(d) < deadband, 0, np.sign(d)).astype(np.int8)
    return quantise(horizontal).tobytes() + quantise(vertical).tobytes()


def hash_levels(tile, steps=6):
    """Luminance flattened to a few bands, normalised per tile so an overall lift does not count."""
    grey = luminance(tile)
    visible = grey[grey >= 0]
    if visible.size == 0:
        return b"blank"
    low, high = float(visible.min()), float(visible.max())
    span = max(1.0, high - low)
    banded = np.where(grey < 0, -1, np.floor((grey - low) / span * (steps - 1) + 0.5))
    return banded.astype(np.int8).tobytes()


CANDIDATES = {
    "exact (shipping)": hash_exact,
    "alpha only": hash_alpha,
    "edge signs": hash_edges,
    "edge signs + deadband": hash_edges_deadband,
    "luminance bands": hash_levels,
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("sheet_a")
    parser.add_argument("sheet_b")
    parser.add_argument("--tiles", help="comma separated tile indices to score; default is every tile both sheets have")
    parser.add_argument("--expect", choices=("match", "differ"), help="what the right answer is, for a pass/fail line")
    args = parser.parse_args()

    a, per_row_a = tiles_of(args.sheet_a)
    b, per_row_b = tiles_of(args.sheet_b)
    if per_row_a != per_row_b:
        print(f"WARNING: sheets are different widths ({per_row_a} vs {per_row_b} tiles), indices will not line up")
    indices = [int(t) for t in args.tiles.split(",")] if args.tiles else sorted(set(a) & set(b))
    indices = [i for i in indices if i in a and i in b]
    if not indices:
        raise SystemExit("no tiles in common")

    print(f"{len(indices)} tiles scored")
    if args.expect:
        print(f"expected answer: {args.expect.upper()}")
    print(f"{'hash':<24}{'same':>8}{'differ':>8}{'% same':>9}   verdict")
    for name, fn in CANDIDATES.items():
        same = sum(1 for i in indices if fn(a[i]) == fn(b[i]))
        differ = len(indices) - same
        share = 100.0 * same / len(indices)
        verdict = ""
        if args.expect == "match":
            verdict = "good" if share > 95 else ("weak" if share > 60 else "FAILS")
        elif args.expect == "differ":
            verdict = "good" if share < 5 else ("weak" if share < 40 else "FAILS")
        print(f"{name:<24}{same:>8}{differ:>8}{share:>8.1f}%   {verdict}")


if __name__ == "__main__":
    main()
