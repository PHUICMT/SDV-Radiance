"""Which water labels a mod's map puts down on art that is not the art they were painted on.

A water label is keyed by sheet NAME and tile index. A pack that repaints a sheet under the same
name, and lays its own map out of it, can put a rock, a flower or a wall where the base game had
water at that index. The label does not know: it paints the water effect over the rock. That is
what Way Back Pelican Town does to the pond by the town's steam-sign building, reported with a
picture by Elacro.

This answers, for one dumped map version, exactly which placed tiles carry a water label and sit
on art that differs from the base game's picture of that tile, so the fix is a worklist rather
than a hunt:

    python waterlabelsonmodart.py Town~1e44de            report the drifted water tiles
    python waterlabelsonmodart.py Town~1e44de --sheet    also write a contact sheet PNG

The base picture is the one the base-game version of the same place uses (the version whose
profiles include Label-BaseArt), because that is the picture the shipped labels were painted on.

Honest about its limits:
  * "differs" means pixels differ; a pure recolour of real water also differs. The contact sheet
    shows base art, mod art and the water mask side by side so a person decides, not this.
  * an animated tile (water frames) is compared on the frame the dump stored.
  * a sheet the base version never places has no base picture here and is reported as such.
"""
import argparse
import base64
import collections
import io
import json
import os
import struct
import sys

from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding="utf-8")

STUDIO = os.path.expanduser(r"~\Documents\HF-Studio")
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WATER_LABELS = os.path.join(REPO, "labels", "water-labels.json")
TILE = 16
# Class indices in water-labels.json that the water pass treats as liquid: water, ice, flowing,
# lava, hot. The same five the fingerprint guard deliberately leaves alone.
LIQUID_CLASSES = {1, 9, 10, 11, 14}
# A pixel counts as changed when any channel moves more than this; below it is compression noise.
PIXEL_CHANGE = 12


def decode_cells(layer):
    raw = base64.b64decode(layer["cells"])
    return struct.unpack("<%di" % (len(raw) // 4), raw)


def tile_pixels(image, index, sheet_width_tiles):
    column, row = index % sheet_width_tiles, index // sheet_width_tiles
    return image.crop((column * TILE, row * TILE, column * TILE + TILE, row * TILE + TILE))


def changed_share(first, second):
    a, b = first.convert("RGBA").tobytes(), second.convert("RGBA").tobytes()
    changed = 0
    for pixel in range(TILE * TILE):
        offset = pixel * 4
        if max(abs(a[offset + channel] - b[offset + channel]) for channel in range(4)) > PIXEL_CHANGE:
            changed += 1
    return changed / (TILE * TILE)


def sheet_art_by_name(location_file):
    return {name: art for name, art in zip(location_file["sheets"], location_file.get("sheetArt") or [])}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("version", help="a map version key in maps.json, e.g. Town~1e44de")
    parser.add_argument("--sheet", action="store_true", help="write a contact sheet PNG")
    parser.add_argument("--top", type=int, default=48, help="how many tiles on the contact sheet")
    arguments = parser.parse_args()

    dump = json.load(open(os.path.join(STUDIO, "maps.json"), encoding="utf-8"))
    locations = dump["locations"]
    version = locations[arguments.version]
    base_key = next((key for key, entry in locations.items()
                     if entry.get("name") == version["name"] and "Label-BaseArt" in (entry.get("from") or [])), None)
    if base_key is None:
        sys.exit(f"no base-game version of {version['name']} in the dump")
    version_file = json.load(open(os.path.join(STUDIO, version["file"]), encoding="utf-8"))
    base_file = json.load(open(os.path.join(STUDIO, locations[base_key]["file"]), encoding="utf-8"))
    version_art, base_art = sheet_art_by_name(version_file), sheet_art_by_name(base_file)

    labels = json.load(open(WATER_LABELS, encoding="utf-8"))["sheets"]
    images = {}

    def open_art(path):
        if path not in images:
            images[path] = Image.open(os.path.join(STUDIO, path)).convert("RGBA")
        return images[path]

    placed = collections.defaultdict(list)          # (sheet, index) -> [(layer, x, y)]
    for layer in version_file["layers"]:
        width = layer["w"]
        for cell_number, value in enumerate(decode_cells(layer)):
            if value < 0:
                continue
            sheet = version_file["sheets"][value >> 20]
            placed[(sheet, value & 0xFFFFF)].append((layer["id"], cell_number % width, cell_number // width))

    drifted, unchanged, no_base = [], 0, collections.Counter()
    for (sheet, index), where in placed.items():
        blob = (labels.get(sheet) or {}).get("tiles", {}).get(str(index))
        if not blob:
            continue
        mask = base64.b64decode(blob)
        liquid_pixels = sum(1 for byte in mask if byte in LIQUID_CLASSES)
        if liquid_pixels == 0:
            continue
        if sheet not in base_art or not base_art[sheet] or not version_art.get(sheet):
            no_base[sheet] += 1
            continue
        mod_image, base_image = open_art(version_art[sheet]), open_art(base_art[sheet])
        sheet_width_tiles = mod_image.width // TILE
        mod_tile = tile_pixels(mod_image, index, sheet_width_tiles)
        base_tile = tile_pixels(base_image, index, base_image.width // TILE)
        share = changed_share(mod_tile, base_tile)
        if share == 0:
            unchanged += 1
            continue
        drifted.append({"sheet": sheet, "index": index, "liquid": liquid_pixels, "changed": share,
                        "where": where, "mod": mod_tile, "base": base_tile, "mask": mask})

    drifted.sort(key=lambda entry: entry["changed"] * entry["liquid"], reverse=True)
    print(f"{arguments.version} ({version['name']}, from {', '.join(version['from'])}) against {base_key}")
    print(f"water-labelled tiles placed: {unchanged + len(drifted) + sum(no_base.values())}"
          f" | same art as the base game: {unchanged} | art differs: {len(drifted)}"
          f" | no base picture: {sum(no_base.values())} {dict(no_base) if no_base else ''}")
    print()
    print("  changed  liquid px  placed  sheet#tile             first places (layer x,y)")
    for entry in drifted:
        places = " ".join(f"{layer} {x},{y}" for layer, x, y in entry["where"][:3])
        print(f"  {entry['changed']:6.0%}  {entry['liquid']:9d}  {len(entry['where']):6d}  "
              f"{entry['sheet'] + '#' + str(entry['index']):22s} {places}")

    if arguments.sheet and drifted:
        zoom, gap = 6, 6
        cell = TILE * zoom
        shown = drifted[:arguments.top]
        sheet_image = Image.new("RGB", (3 * cell + 4 * gap + 330, len(shown) * (cell + gap) + gap + 20), (18, 18, 22))
        draw = ImageDraw.Draw(sheet_image)
        draw.text((gap, 4), "base art | mod art | mod art with the water label tinted", fill=(230, 230, 230))
        for row, entry in enumerate(shown):
            top = 20 + gap + row * (cell + gap)
            tinted = entry["mod"].copy()
            overlay = Image.new("RGBA", (TILE, TILE))
            overlay.putdata([(40, 140, 255, 150) if byte in LIQUID_CLASSES else (0, 0, 0, 0) for byte in entry["mask"]])
            tinted = Image.alpha_composite(tinted, overlay)
            for column, picture in enumerate((entry["base"], entry["mod"], tinted)):
                sheet_image.paste(picture.convert("RGB").resize((cell, cell), Image.NEAREST),
                                  (gap + column * (cell + gap), top))
            first = entry["where"][0]
            draw.text((4 * gap + 3 * cell, top + 4),
                      f"{entry['sheet']}#{entry['index']}\nchanged {entry['changed']:.0%}, {entry['liquid']} liquid px\n"
                      f"placed {len(entry['where'])}x, e.g. {first[0]} {first[1]},{first[2]}",
                      fill=(230, 230, 230))
        output = os.path.join(STUDIO, f"water-drift-{arguments.version}.png")
        sheet_image.save(output)
        print()
        print("contact sheet ->", output)


if __name__ == "__main__":
    main()
