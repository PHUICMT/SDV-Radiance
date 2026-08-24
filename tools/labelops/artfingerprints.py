"""Tell the mod every picture a label is still right for, so a recolour does not lose it.

    python tools/labelops/artfingerprints.py           measure and report, write nothing
    python tools/labelops/artfingerprints.py --write   write labels/art-fingerprints.json

A label is keyed by sheet NAME and tile index, and a name is not a picture: this corpus holds 111
pictures called spring_outdoorsTileSheet and 29 called spring_beach. The mod already knows which
one is loaded - it hashes the live texture through GetData - but knowing the art changed does not
tell it where the water is in the new art, so all it can do is refuse.

MOST OF THAT REFUSING IS UNNECESSARY, and this is what fixes it for free. Measured over the tiles
that carry a label and that some map actually places:

    8,630 tiles on names with more than one picture
    5,133 differ in pixels between pictures      <- a recolour differs in pixels
    3,830 still differ once colour is ignored

Earthy and DaisyNiko repaint a tile and leave the water exactly where it was; the label is as
right afterwards as before, and the only reason the mod cannot tell is that nobody listed the
repaint's fingerprint next to the original's. This lists them. The file format already allows it -
a tile maps to a LIST of fingerprints and the mod accepts the label if the live art is any of
them - so nothing on the C# side changes.

WHAT COUNTS AS THE SAME DRAWING is twinlabels' shading key: the alpha outline, plus luminance
quantised to four levels, which is what survives a palette swap and what a different picture does
not. Colour is ignored on purpose. A tile drawn differently is left out of the group and needs a
label of its own, which is art-variants.json and a person, not this.

DOING IT HERE IS WHAT MAKES IT SAFE. The same idea was briefly built into the mod as a second
chance at match time - if the fingerprint disagrees, ask whether the drawing does - and it was
removed for rescuing nothing: with the repaints already listed below, every pair that still
disagrees disagrees about the drawing too. What it did instead was forgive redrawn OPAQUE tiles,
whose silhouette matches every other opaque tile there has ever been.

The fingerprints already in the file were captured IN THE GAME through Texture2D.GetData, which is
the only reading that can be trusted absolutely, and they are kept exactly as they are. This adds
to them. That the two agree was checked rather than assumed: over the tiles where premultiplied
and straight alpha give different bytes, the straight reading this tool takes from the dumped PNGs
matched the in-game value 27 times against premultiplied's 4.
"""
import argparse, collections, glob, hashlib, io, json, os, sys

import numpy
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twinlabels as labels
import blackcells as black

REPO = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(REPO, "labels", "art-fingerprints.json")
TILE = 16
CELL_SHEET_STRIDE = 0x100000
FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211
FNV_MASK = (1 << 64) - 1


def fingerprint(block):
    """FNV-1a 64 bit over the 256 pixels packed R,G,B,A - src/Shared/ArtFingerprint.cs, exactly."""
    hash_value = FNV_OFFSET
    for value in block.reshape(-1).tolist():
        hash_value = ((hash_value ^ int(value)) * FNV_PRIME) & FNV_MASK
    return f"{hash_value:016x}"


def both_alpha_readings(block):
    """The tile's fingerprint under BOTH alpha conventions, because only one of them is ours.

    The game hashes what Texture2D.GetData hands back, which is premultiplied; this reads a PNG,
    which is not. For an opaque tile the two are the same number and this returns one value. For a
    tile with soft edges they differ, and which one the live texture will produce is not something
    this side can know: measured against the fingerprints the game itself recorded, the straight
    reading was right 27 times and the premultiplied 4, and 13 tiles matched neither.

    Listing both costs a few bytes on the tiles that have soft edges and closes the one way this
    file can be actively wrong - telling the mod a label does not belong to art it does belong to,
    which takes the glass out of a window that was correct all along.
    """
    straight = fingerprint(block)
    alpha = block[:, :, 3].astype(numpy.uint16)
    premultiplied = block.astype(numpy.uint16).copy()
    for channel in range(3):
        premultiplied[:, :, channel] = (premultiplied[:, :, channel] * alpha + 127) // 255
    other = fingerprint(premultiplied.astype(numpy.uint8))
    return {straight} if other == straight else {straight, other}


def placed_tiles(index):
    """sheet name -> the tile indices some map actually draws. Labels on the rest change no pixel."""
    placed = collections.defaultdict(set)
    for entry in index["locations"].values():
        document = labels.read_location(entry)
        names = document.get("sheets") or []
        for layer in document.get("layers") or []:
            for value in black.read_cells(layer):
                if value < 0:
                    continue
                slot, tile = divmod(value, CELL_SHEET_STRIDE)
                if slot < len(names):
                    placed[names[slot]].add(tile)
    return placed


def labelled_tiles():
    """sheet name -> the tile indices somebody has painted."""
    out = {}
    for path in labels.label_files().values():
        name = os.path.basename(path)[:-len(".labels.json")]
        document = labels.read_labels(path)
        if document and isinstance(document.get("tiles"), dict):
            out[name] = {int(key) for key in document["tiles"]}
    return out


def pixels(path):
    try:
        return numpy.array(Image.open(os.path.join(labels.HFDIR, path)).convert("RGBA"))
    except Exception:
        return None


def block_of(image, tile):
    columns, rows = image.shape[1] // TILE, image.shape[0] // TILE
    if columns <= 0 or tile >= columns * rows:
        return None
    y, x = divmod(tile, columns)
    return image[y * TILE:(y + 1) * TILE, x * TILE:(x + 1) * TILE]


def group_for_sheet(name, arts, reference_art, wanted):
    """tile -> the fingerprints of every picture drawing the same thing as the reference does.

    Same WIDTH only: a tile index means a position once the column count is known, so a picture of
    another width is not a different palette, it is a different indexing and its tile 240 is
    somewhere else entirely.
    """
    reference = pixels(reference_art)
    if reference is None:
        return {}, 0, 0
    width = reference.shape[1] // TILE
    keys, prints = {}, collections.defaultdict(set)
    for tile in wanted:
        block = block_of(reference, tile)
        if block is None:
            continue
        # A tile with nothing visible in it hashes the same as every other empty tile, so listing
        # it would tell the mod "this art matches" about art it has never seen. It also has no
        # label worth defending: nothing is drawn there.
        if not (block[:, :, 3] > labels.ALPHA_FLOOR).any():
            continue
        key = labels.shading_key(block)
        if key is None:
            continue
        keys[tile] = key
        prints[tile] |= both_alpha_readings(block)
    carried = 0
    for art in arts:
        if art == reference_art:
            continue
        image = pixels(art)
        if image is None or image.shape[1] // TILE != width:
            continue
        for tile, key in keys.items():
            block = block_of(image, tile)
            if block is None or labels.shading_key(block) != key:
                continue
            before = len(prints[tile])
            prints[tile] |= both_alpha_readings(block)
            carried += len(prints[tile]) - before
    return {tile: sorted(values) for tile, values in prints.items() if values}, len(keys), carried


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    index = labels.load_index()
    by_name = index.get("artPng") or {}
    versions = labels.sheet_versions(index)
    placed = placed_tiles(index)
    painted = labelled_tiles()

    existing = {}
    if os.path.exists(OUT):
        existing = json.load(io.open(OUT, encoding="utf-8"))
    kept = existing.get("sheets") or {}

    sheets, tiles_out, carried_total, skipped = {}, 0, 0, 0
    for name, wanted in sorted(painted.items()):
        wanted = sorted(wanted & placed.get(name, set()))
        if not wanted:
            continue
        reference = by_name.get(name)
        if not reference:
            skipped += 1
            continue
        found, looked, carried = group_for_sheet(name, sorted(versions.get(name, ())),
                                                 reference, wanted)
        if not found:
            continue
        # Everything the game itself measured stays, and is listed first. It came out of GetData
        # on a real texture, which is the one reading nothing can argue with.
        merged = {}
        for tile, values in found.items():
            was = kept.get(name, {}).get(str(tile))
            was = [was] if isinstance(was, str) else list(was or [])
            merged[str(tile)] = was + [v for v in values if v not in was]
        for tile, values in (kept.get(name) or {}).items():
            merged.setdefault(tile, [values] if isinstance(values, str) else list(values))
        sheets[name] = dict(sorted(merged.items(), key=lambda kv: int(kv[0])))
        tiles_out += len(merged)
        carried_total += carried
    for name, byTile in kept.items():
        sheets.setdefault(name, byTile)

    total_prints = sum(len(v) for byTile in sheets.values() for v in byTile.values())
    print(f"{len(sheets)} sheet(s), {tiles_out:,} tile(s) measured here, "
          f"{total_prints:,} fingerprint(s) in all")
    print(f"  carried onto a repaint that draws the same thing: {carried_total:,}")
    if skipped:
        print(f"  {skipped} labelled sheet(s) have no art in the dump and were left alone")
    widest = sorted(((sum(len(v) for v in byTile.values()), name)
                     for name, byTile in sheets.items()), reverse=True)[:10]
    print()
    for count, name in widest:
        print(f"  {count:>6} fingerprint(s)  {name}")

    if not arguments.write:
        print("\npass --write to update labels/art-fingerprints.json")
        return
    document = dict(existing)
    # Dropped by name, not merely left unwritten. This file is built by copying what is already on
    # disk and overwriting the parts this tool owns, so a key it USED to write survives forever
    # unless it is removed - and a stale silhouette table would look like data the mod still reads.
    document.pop("outlines", None)
    document["format"] = existing.get("format", 1)
    document["sheets"] = dict(sorted(sheets.items()))
    document["carriedOntoRepaints"] = ("Fingerprints added by tools/labelops/artfingerprints.py: "
                                       "pictures of the same sheet whose tile has the same outline "
                                       "and the same shading, which is what a repaint keeps. The "
                                       "ones measured in the game are listed first.")
    with io.open(OUT, "w", encoding="utf-8") as handle:
        json.dump(document, handle, ensure_ascii=False, separators=(",", ":"))
    print(f"\nwrote {OUT}  ({os.path.getsize(OUT) / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
