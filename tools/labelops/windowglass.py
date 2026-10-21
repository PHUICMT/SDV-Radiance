"""Write labels/window-glass.json: which pixels of a window tile are glass, for the window light.

    python tools/labelops/windowglass.py <corpus review folder> [prefix ...]
    python tools/labelops/windowglass.py E:\\Games\\GamesMods\\DevStardew\\_window-corpus\\review focus-vanilla focus-sve

The glass comes from comparing a window's two pictures (day against rain or night): the pixels that
change are the glass, the frame, bars and curtains stay the same. That comparison is done by the
window corpus tools (.snapshot-smapi/windowcorpus), which write <prefix>-windows.jsonl with one row
per distinct window picture; this reads those rows, cuts each window into 16x16 tiles, and keys
every tile by its sheet name and tile index, the way the other label files are keyed.

A window checked by hand in the glass labeler (tools/windowglass/labeler.py) has its own file in
<corpus>/glass-labels/<digest>.json, and that answer wins over the automatic one: "no-glass" drops
the window, "auto-ok" and "edited" use the mask that was saved. A window with no pair, which the
automatic answer leaves out, is written too once someone has painted its glass.

It is a file of its own rather than class 12 in water-labels.json on purpose: class 12 also turns
on glass reflections, and an interior window gaining a reflection is a change nobody asked for.
Every tile carries the fingerprints of the art it was cut from (artfingerprints.alpha_readings),
so a pack that repaints the sheet is not handed glass in the wrong place.

Different pictures can carry the same sheet name: every Alternative Textures window is a
"texture_1.png", and a recolour of the game's furniture is another "furniture.png". Their glass is
kept apart, as variants of the tile, each with its own art, and the game picks the one whose art it
is drawing. Adding them up had given every one of those windows the glass of all the others.
"""
import base64, json, os, sys
import numpy
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from artfingerprints import alpha_readings

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(REPO, "labels", "window-glass.json")
TILE = 16


def checked_by_hand(folder):
    labels = os.path.join(os.path.dirname(os.path.abspath(folder)), "glass-labels")
    checked = {}
    if os.path.isdir(labels):
        for file in os.listdir(labels):
            if file.endswith(".json"):
                label = json.load(open(os.path.join(labels, file), encoding="utf-8"))
                checked[label["digest"]] = label
    return checked


CURTAIN_MODS = set()


def rows(folder, prefixes):
    # The same picture can sit in more than one review file, each listing the placements it saw;
    # every row is read, so no placement is lost, and the glass of repeated rows adds up.
    checked = checked_by_hand(folder)
    by_hand = automatic = dropped = 0
    for prefix in prefixes:
        path = os.path.join(folder, f"{prefix}-windows.jsonl")
        for line in open(path, encoding="utf-8"):
            row = json.loads(line)
            if row.get("kind") == "floor-sunlight":
                continue
            label = checked.get(row.get("digest"))
            if label:
                painted = any(int(c, 16) for c in label["mask"] + label.get("sheer", ""))
                # Checked and found to have no glass: written as tiles with none, so the game knows
                # this window and gives it no light, rather than the old light of a window it has
                # never heard of.
                if label["status"] == "no-glass" or not painted:
                    dropped += 1
                    yield {**row, "mask": "", "sheer": "", "noGlass": True}
                    continue
                by_hand += 1
                if any(int(c, 16) for c in label.get("sheer", "")):
                    CURTAIN_MODS.add(str(row.get("mod")))
                yield {**row, "mask": label["mask"], "sheer": label.get("sheer", "")}
            elif row.get("method") in ("pair", "see-through") and row.get("glassPixels", 0) > 0:
                automatic += 1
                yield row
    print(f"{by_hand} windows checked by hand, {automatic} automatic, {dropped} known to have no glass")


def mask_of(row, key="mask"):
    height, width = row["maskShape"]
    if not row.get(key):
        return numpy.zeros((height, width), bool)
    bits = numpy.unpackbits(numpy.frombuffer(bytes.fromhex(row[key]), numpy.uint8))[:height * width]
    return bits.reshape(height, width).astype(bool)


def add_tile(sheets, pictures, picture, x, y, tile_mask, sheer_mask, known_clear=False):
    if tile_mask.shape != (TILE, TILE) or not (tile_mask.any() or sheer_mask.any() or known_clear) or x % TILE or y % TILE:
        return
    if picture not in pictures:
        pictures[picture] = numpy.array(Image.open(picture).convert("RGBA"))
    art = pictures[picture]
    if not (art[y:y + TILE, x:x + TILE, 3] > 0).any():
        return
    sheet_name = os.path.splitext(os.path.basename(picture))[0]
    entry = sheets.setdefault(sheet_name, {"size": [art.shape[1], art.shape[0]], "tiles": {}})
    index = (y // TILE) * (art.shape[1] // TILE) + x // TILE
    variants = entry["tiles"].setdefault(str(index), {})
    tile = variants.setdefault(os.path.normcase(os.path.abspath(picture)), {"glass": numpy.zeros((TILE, TILE), bool),
                                                                            "sheer": numpy.zeros((TILE, TILE), bool),
                                                                            "colour": numpy.zeros((TILE, TILE, 3), numpy.uint8),
                                                                            "art": set()})
    block = art[y:y + TILE, x:x + TILE]
    tile["glass"] |= tile_mask
    tile["sheer"] |= sheer_mask
    tile["colour"][sheer_mask] = block[..., :3][sheer_mask]
    tile["art"] |= alpha_readings(block)


def main():
    folder, prefixes = sys.argv[1], sys.argv[2:] or ["focus-vanilla", "focus-sve"]
    sheets, pictures = {}, {}
    for row in rows(folder, prefixes):
        mask = mask_of(row)
        sheer = mask_of(row, "sheer")
        placements = [row] + row.get("copies", [])
        for placement in placements:
            # A map window put back together from its tiles: each piece goes back to its own tile
            # of its own sheet, by day and by night.
            for piece in placement.get("pieces", []):
                offset_x, offset_y = piece["offset"]
                for corner in (piece["day"], piece["night"]):
                    add_tile(sheets, pictures, piece["sheet"], corner[0], corner[1],
                             mask[offset_y:offset_y + TILE, offset_x:offset_x + TILE],
                             sheer[offset_y:offset_y + TILE, offset_x:offset_x + TILE], row.get("noGlass", False))
        for placement, rect_key, picture_key in [(p, r, k) for p in placements if "pieces" not in p
                                                 for r, k in (("rect", "picture"), ("pair", "pairPicture"))]:
            rect, picture = placement.get(rect_key), placement.get(picture_key)
            if not rect or not picture:
                continue
            x0, y0, width, height = rect
            if x0 % TILE or y0 % TILE:
                continue
            for dy in range(0, height, TILE):
                for dx in range(0, width, TILE):
                    add_tile(sheets, pictures, picture, x0 + dx, y0 + dy,
                             mask[dy:dy + TILE, dx:dx + TILE], sheer[dy:dy + TILE, dx:dx + TILE], row.get("noGlass", False))
    written = {"format": "16x16-glass-bits-base64",
               "//": "Glass pixels of window tiles, for the window light. Bit order: row by row, top left first, "
                     "most significant bit first (numpy.packbits). 'sheer' (optional) marks a sheer curtain, which "
                     "lets part of the light through; a pixel that is glass is never sheer. 'sheerColour' is the "
                     "curtain's own colour, three bytes (red, green, blue) per pixel in the same order, zero off the "
                     "curtain, so the light through it can take its colour. 'art' lists the fingerprints of the art the glass "
                     "was found on; a tile whose live art matches none of them is not given glass. A tile whose "
                     "sheet name is shared by different pictures lists them under 'variants', each with its own "
                     "glass and art. A tile or variant with no glass bits is a window checked and found to have "
                     "none. 'curtainMods' lists the mods (Nexus ids, or 'vanilla') with a sheer curtain on "
                     "some window. Written by tools/labelops/windowglass.py.",
               "curtainMods": [],
               "sheets": {}}
    tiles = 0
    for sheet_name, entry in sorted(sheets.items()):
        out_tiles = {}
        for index, variants in sorted(entry["tiles"].items(), key=lambda pair: int(pair[0])):
            # Pictures that came to the same glass are one variant, with the art of all of them.
            written_variants = {}
            for tile in variants.values():
                variant = {"glass": base64.b64encode(numpy.packbits(tile["glass"].reshape(-1)).tobytes()).decode()}
                sheer_only = tile["sheer"] & ~tile["glass"]
                if sheer_only.any():
                    variant["sheer"] = base64.b64encode(numpy.packbits(sheer_only.reshape(-1)).tobytes()).decode()
                    colour = numpy.where(sheer_only[..., None], tile["colour"], 0).astype(numpy.uint8)
                    variant["sheerColour"] = base64.b64encode(colour.tobytes()).decode()
                key = (variant["glass"], variant.get("sheer", ""), variant.get("sheerColour", ""))
                written_variants.setdefault(key, {**variant, "art": set()})["art"] |= tile["art"]
            ordered = [{**variant, "art": sorted(variant["art"])} for _, variant in sorted(written_variants.items())]
            out_tiles[index] = ordered[0] if len(ordered) == 1 else {"variants": ordered}
            tiles += 1
        if out_tiles:
            written["sheets"][sheet_name] = {"size": entry["size"], "tiles": out_tiles}
    # Near the top on purpose: the game reads only the head of the file at launch to learn
    # whether any window with a sheer curtain is installed, and hides the curtain dials if not.
    written["curtainMods"] = sorted(CURTAIN_MODS)
    with open(OUT, "w", encoding="utf-8", newline="\n") as file:
        json.dump(written, file, indent=1, ensure_ascii=False)
        file.write("\n")
    print(f"{tiles} glass tiles on {len(written['sheets'])} sheets -> {os.path.relpath(OUT, REPO)}")
    for sheet_name, entry in written["sheets"].items():
        print(f"  {sheet_name:30s} {len(entry['tiles'])} tiles")


if __name__ == "__main__":
    main()
