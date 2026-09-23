"""Per mod: where a painted label stops working, in the dumped season and in the three others.

A label painted for a mod's art (a variant, in labels/art-variants.json) is tied to the
fingerprint of the exact picture it was painted on. The corpus is a SPRING dump, so a variant is
tied to the spring picture and, where the page fanned it out, sometimes to summer. A pack that
repaints fall_town as well as spring_town hands the game a fall picture no variant names, and in
fall the tile falls back to the base game's label - which describes the base game's art. Where
that label says water and the pack drew rock, the water effect lands on the rock. That is the hot
spring by the railroad in Way Back Pelican Town, reported by Elacro with an autumn picture, on
tiles that were labelled correctly for spring.

Two kinds of gap, counted per mod:

  SPRING GAP   a placed tile in the pack's own dumped map, on art that differs from the base
               game's, carrying a base label with liquid in it, and no variant for that art.
               Nothing covers it in any season.
  SEASON GAP   a tile a spring variant does cover, on a sheet the pack ALSO repaints for another
               season (read from its content.json), where that season has no variant for the tile.
               Covered in spring, uncovered there.

For each gap the pixels that matter are counted both ways: LIQUID ON ART the fallback label
paints water where the pack's art has none (the visible bug), and WATER MISSED the pack's art
has water the fallback does not cover (the effect is simply absent).

    python seasonlabelgaps.py              every mod, worst first
    python seasonlabelgaps.py --mod "Way Back"   one mod, tile by tile

Honest about its limits:
  * a season gap assumes the pack keeps the same layout across seasons, which a map does; the
    pixel counts compare the spring variant with the fallback label, not with the pack's actual
    fall art, which this corpus does not hold.
  * "repaints that season" is read from Content Patcher targets; a pack that edits art from C#
    is invisible, and a patch switched off in config still counts.
"""
import argparse
import base64
import collections
import json
import os
import re
import struct
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mapartmods import claims_in  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

STUDIO = os.path.expanduser(r"~\Documents\HF-Studio")
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
TILE = 16
LIQUID_CLASSES = {1, 9, 10, 11, 14}
SEASONS = ("spring", "summer", "fall", "winter")
SEASONAL_NAME = re.compile(r"(?i)^(spring|summer|fall|winter)_(.+)$")
FNV_OFFSET, FNV_PRIME, FNV_MASK = 14695981039346656037, 1099511628211, 0xFFFFFFFFFFFFFFFF


def decode_cells(layer):
    raw = base64.b64decode(layer["cells"])
    return struct.unpack("<%di" % (len(raw) // 4), raw)


def liquid_set(mask):
    return {pixel for pixel, byte in enumerate(mask) if byte in LIQUID_CLASSES}


def looks_like_water(red, green, blue):
    """Blue or teal dominant: what water is in the base game and in every recolour measured so far.
    Rock, soil, wood, leaves and flowers are not. A heuristic, which is why the report calls the
    spring column an estimate and the contact sheets exist."""
    return blue - red >= 25 and blue - green >= -30


def liquid_on_dry_art(mask, pixels):
    """Liquid label pixels that land on opaque art that does not look like water."""
    count = 0
    for pixel in liquid_set(mask):
        offset = pixel * 4
        red, green, blue, alpha = pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]
        if alpha > 0 and not looks_like_water(red, green, blue):
            count += 1
    return count


class ArtReader:
    """Tile pixels and fingerprints, opened and hashed once per picture and tile."""

    def __init__(self):
        self._images, self._tiles = {}, {}

    def tile(self, path, index):
        key = (path, index)
        if key not in self._tiles:
            if path not in self._images:
                self._images[path] = Image.open(os.path.join(STUDIO, path)).convert("RGBA")
            image = self._images[path]
            width_tiles = image.width // TILE
            x, y = (index % width_tiles) * TILE, (index // width_tiles) * TILE
            if y + TILE > image.height:
                self._tiles[key] = (None, None)
            else:
                pixels = image.crop((x, y, x + TILE, y + TILE)).tobytes()
                fingerprint = FNV_OFFSET
                for byte in pixels:
                    fingerprint = ((fingerprint ^ byte) * FNV_PRIME) & FNV_MASK
                self._tiles[key] = (pixels, f"{fingerprint:016x}")
        return self._tiles[key]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mod", help="only mods whose folder contains this text, tile by tile")
    arguments = parser.parse_args()

    dump = json.load(open(os.path.join(STUDIO, "maps.json"), encoding="utf-8"))
    pass_list = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "passes.json"),
                               encoding="utf-8"))["passes"]
    passes = {entry["name"]: entry.get("mod") for entry in pass_list}
    pass_size = {entry["name"]: entry.get("modCount") or 9999 for entry in pass_list}
    labels = json.load(open(os.path.join(REPO, "labels", "water-labels.json"), encoding="utf-8"))["sheets"]
    variants = json.load(open(os.path.join(REPO, "labels", "art-variants.json"), encoding="utf-8"))["sheets"]
    reader = ArtReader()

    # The base game's picture of every sheet name, from the versions the baseline pass produced.
    base_art = {}
    for entry in dump["locations"].values():
        if "Label-BaseArt" not in (entry.get("from") or []):
            continue
        location = json.load(open(os.path.join(STUDIO, entry["file"]), encoding="utf-8"))
        for name, art in zip(location["sheets"], location.get("sheetArt") or []):
            if art and name not in base_art:
                base_art[name] = art

    def base_label(name, index):
        blob = (labels.get(name) or {}).get("tiles", {}).get(str(index))
        return base64.b64decode(blob) if blob else None

    def variant_for(name, index, fingerprint):
        for entry in (variants.get(name) or {}).get(str(index), []):
            if fingerprint in (entry.get("art") or []):
                return base64.b64decode(entry["label"])
        return None

    spring_gaps = collections.defaultdict(dict)     # mod -> {(sheet, tile): (liquid on art, where)}
    covered = collections.defaultdict(dict)         # mod -> {(sheet, tile): variant label}
    versions_per_mod = collections.Counter()
    for key, entry in dump["locations"].items():
        origin = entry.get("from") or []
        if "Label-BaseArt" in origin:
            continue
        # A version reached by several passes belongs to the smallest of them: every pack that
        # depends on Stardew Valley Expanded has SVE's maps in its pass, and naming all of them
        # would list one fault thirty times under thirty names.
        candidates = [name for name in origin if passes.get(name)]
        if not candidates:
            continue
        smallest = min(pass_size[name] for name in candidates)
        mods = sorted({passes[name] for name in candidates if pass_size[name] == smallest})
        if arguments.mod and not any(arguments.mod.lower() in mod.lower() for mod in mods):
            continue
        location = json.load(open(os.path.join(STUDIO, entry["file"]), encoding="utf-8"))
        art_of = dict(zip(location["sheets"], location.get("sheetArt") or []))
        for mod in mods:
            versions_per_mod[mod] += 1
        for layer in location["layers"]:
            width = layer["w"]
            for cell, value in enumerate(decode_cells(layer)):
                if value < 0:
                    continue
                name = location["sheets"][value >> 20]
                index = value & 0xFFFFF
                art = art_of.get(name)
                if not art:
                    continue
                label = base_label(name, index)
                has_variants = str(index) in (variants.get(name) or {})
                if label is None and not has_variants:
                    continue
                pixels, fingerprint = reader.tile(art, index)
                if pixels is None:
                    continue
                own = variant_for(name, index, fingerprint)
                if own is not None:
                    for mod in mods:
                        covered[mod][(name, index)] = own
                    continue
                if label is None or not liquid_set(label):
                    continue
                if base_art.get(name):
                    base_pixels, _ = reader.tile(base_art[name], index)
                    if base_pixels == pixels:
                        continue
                dry = liquid_on_dry_art(label, pixels)
                if dry == 0:
                    continue
                where = f"{entry['name']} {cell % width},{cell // width}"
                for mod in mods:
                    spring_gaps[mod].setdefault((name, index), (dry, where))

    claims_by_mod = {}
    for mod in set(spring_gaps) | set(covered):
        found = set()
        for sheets in claims_in(os.path.join(GAME, "Mods", mod)).values():
            found |= sheets
        for sheets in claims_in(os.path.join(GAME, "Mods (disabled)", mod)).values():
            found |= sheets
        claims_by_mod[mod] = {sheet.lower() for sheet in found}

    rows, detail = [], collections.defaultdict(list)
    for mod in set(spring_gaps) | set(covered):
        season_counts = {season: [0, 0, 0] for season in SEASONS[1:]}   # tiles, liquid on art, water missed
        for (name, index), own in covered[mod].items():
            match = SEASONAL_NAME.match(name)
            if not match:
                continue
            family = match.group(2)
            for season in SEASONS[1:]:
                other = f"{season}_{family}"
                if other.lower() not in claims_by_mod[mod]:
                    continue
                if str(index) in (variants.get(other) or {}):
                    continue
                fallback = base_label(other, index) or base_label(name, index) or bytes(TILE * TILE)
                liquid_on_art = len(liquid_set(fallback) - liquid_set(own))
                water_missed = len(liquid_set(own) - liquid_set(fallback))
                if liquid_on_art == 0 and water_missed == 0:
                    continue
                counts = season_counts[season]
                counts[0] += 1
                counts[1] += liquid_on_art
                counts[2] += water_missed
                detail[mod].append((season, other, index, liquid_on_art, water_missed))
        spring_tiles = len(spring_gaps[mod])
        spring_pixels = sum(value[0] for value in spring_gaps[mod].values())
        worst = spring_pixels + sum(counts[1] for counts in season_counts.values())
        if worst == 0 and spring_tiles == 0:
            continue
        rows.append((worst, mod, spring_tiles, spring_pixels, season_counts, len(covered[mod])))

    rows.sort(reverse=True)
    print(f"{len(rows)} mod(s) with a label gap, worst first. Pixels are liquid painted on art that has none;"
          " the spring column is estimated from pixel colour, the season columns from the painted labels.")
    print()
    print(f"{'mod':52s} {'spring gap':>15s} {'summer':>13s} {'fall':>13s} {'winter':>13s}  covered")
    for worst, mod, spring_tiles, spring_pixels, season_counts, covered_count in rows:
        cells = [f"{spring_tiles:4d}t {spring_pixels:6d}px"]
        for season in SEASONS[1:]:
            tiles, on_art, _ = season_counts[season]
            cells.append(f"{tiles:3d}t {on_art:6d}px" if tiles else f"{'-':>13s}")
        print(f"{mod[-52:]:52s} {cells[0]:>15s} {cells[1]:>13s} {cells[2]:>13s} {cells[3]:>13s}  {covered_count:6d}")

    if arguments.mod:
        for worst, mod, *_ in rows:
            print()
            print(f"== {mod}")
            for (name, index), (pixels, where) in sorted(spring_gaps[mod].items()):
                print(f"  spring gap  {name}#{index:<5d} {pixels:4d} liquid px on changed art, e.g. {where}")
            for season, other, index, on_art, missed in sorted(detail[mod]):
                print(f"  {season:6s} gap  {other}#{index:<5d} {on_art:4d} px water on art, {missed:4d} px water missed")


if __name__ == "__main__":
    main()
