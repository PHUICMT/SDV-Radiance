"""Name the content pack each painted variant was painted for, so the mod can follow it by pack.

A variant in labels/art-variants.json is tied to the fingerprints of the pictures it was painted
on. That is exact and it is narrow: a pack that paints one tile in four seasons, two weathers and
seven palettes hands the game fifty six pictures of it, and the dump holds one. The mod can ask
Content Patcher which pack is painting a tile right now (src/Shared/ContentPatcherArtOwners.cs);
this is the other half, which tells it which pack a variant belongs to.

The per-mod dump already knows. Every map version it holds says which passes produced it, and a
pass is one mod and its dependencies. A variant whose fingerprint appears in a version from
Solo-043 was painted on Way Back Pelican Town's art. Where several passes produce one version,
the smallest of them owns it, so a map Stardew Valley Expanded brings is credited to SVE and not
to every pack that depends on it.

    python variantowners.py            report which packs own how many variants
    python variantowners.py --write    write the "mods" lists into art-variants.json

Run it after every `artvariants.py --write`: that one rebuilds the file from HF Studio's copy,
which has never heard of "mods", so the attribution has to be written again on top.

The ids written are the manifest UniqueIDs of every pack in the owning mod's folder, which is what
Content Patcher reports as a patch's ContentPack. A variant no dumped map places keeps no "mods"
and only ever follows its fingerprints, exactly as before.
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

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
STUDIO = os.path.expanduser(r"~\Documents\HF-Studio")
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
VARIANTS = os.path.join(REPO, "labels", "art-variants.json")
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
TILE = 16
FNV_OFFSET, FNV_PRIME, FNV_MASK = 14695981039346656037, 1099511628211, 0xFFFFFFFFFFFFFFFF
UNIQUE_ID = re.compile(r'"UniqueID"\s*:\s*"([^"]+)"', re.IGNORECASE)


def decode_cells(layer):
    raw = base64.b64decode(layer["cells"])
    return struct.unpack("<%di" % (len(raw) // 4), raw)


def pack_ids(mod_folder):
    """Every manifest UniqueID under a mod's folder, parked or enabled."""
    found = set()
    for root in (os.path.join(GAME, "Mods", mod_folder), os.path.join(GAME, "Mods (disabled)", mod_folder)):
        for dirpath, _, files in os.walk(root):
            if "manifest.json" in files:
                try:
                    text = open(os.path.join(dirpath, "manifest.json"), encoding="utf-8-sig", errors="replace").read()
                except OSError:
                    continue
                match = UNIQUE_ID.search(text)
                if match:
                    found.add(match.group(1))
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    dump = json.load(open(os.path.join(STUDIO, "maps.json"), encoding="utf-8"))
    pass_list = json.load(open(os.path.join(HERE, "passes.json"), encoding="utf-8"))["passes"]
    mod_of_pass = {entry["name"]: entry.get("mod") for entry in pass_list}
    size_of_pass = {entry["name"]: entry.get("modCount") or 9999 for entry in pass_list}
    document = json.load(open(VARIANTS, encoding="utf-8"))
    variants = document["sheets"]
    by_lower = {name.lower(): name for name in variants}

    # (sheet, tile) -> {fingerprint: [entries]} so a placed tile finds its entries in one lookup.
    entries_by_print = collections.defaultdict(lambda: collections.defaultdict(list))
    for name, tiles in variants.items():
        for tile, entries in tiles.items():
            for entry in entries:
                for fingerprint in entry.get("art") or []:
                    entries_by_print[(name, int(tile))][fingerprint].append(entry)

    images, prints = {}, {}

    def fingerprint(path, index):
        key = (path, index)
        if key not in prints:
            if path not in images:
                images[path] = Image.open(os.path.join(STUDIO, path)).convert("RGBA")
            image = images[path]
            width = image.width // TILE
            x, y = (index % width) * TILE, (index // width) * TILE
            if y + TILE > image.height:
                prints[key] = None
            else:
                value = FNV_OFFSET
                for byte in image.crop((x, y, x + TILE, y + TILE)).tobytes():
                    value = ((value ^ byte) * FNV_PRIME) & FNV_MASK
                prints[key] = f"{value:016x}"
        return prints[key]

    ids_of_mod = {}
    owners = collections.defaultdict(set)          # id(entry) -> pack ids
    entry_of_id = {}
    for entry in dump["locations"].values():
        origin = [name for name in (entry.get("from") or []) if mod_of_pass.get(name)]
        if not origin or "Label-BaseArt" in (entry.get("from") or []):
            continue
        smallest = min(size_of_pass[name] for name in origin)
        mods = {mod_of_pass[name] for name in origin if size_of_pass[name] == smallest}
        ids = set()
        for mod in mods:
            if mod not in ids_of_mod:
                ids_of_mod[mod] = pack_ids(mod)
            ids |= ids_of_mod[mod]
        if not ids:
            continue
        location = json.load(open(os.path.join(STUDIO, entry["file"]), encoding="utf-8"))
        art_of = dict(zip(location["sheets"], location.get("sheetArt") or []))
        for layer in location["layers"]:
            for value in decode_cells(layer):
                if value < 0:
                    continue
                sheet = location["sheets"][value >> 20]
                index = value & 0xFFFFF
                real = by_lower.get(sheet.lower())
                if real is None or str(index) not in variants[real] or not art_of.get(sheet):
                    continue
                live = fingerprint(art_of[sheet], index)
                for owned in entries_by_print[(real, index)].get(live, []):
                    owners[id(owned)] |= ids
                    entry_of_id[id(owned)] = owned

    total = sum(len(entries) for tiles in variants.values() for entries in tiles.values())
    per_pack = collections.Counter()
    for key, ids in owners.items():
        for pack in ids:
            per_pack[pack] += 1
    print(f"{len(owners)} of {total} variant label(s) placed by a dumped map and so attributed")
    for pack, count in per_pack.most_common():
        print(f"  {count:5d}  {pack}")

    if arguments.write:
        for key, ids in owners.items():
            entry = entry_of_id[key]
            entry["mods"] = sorted(set(entry.get("mods") or []) | ids)
        with open(VARIANTS, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(document, handle, ensure_ascii=False, separators=(",", ":"))
        print("wrote", VARIANTS)


if __name__ == "__main__":
    main()
