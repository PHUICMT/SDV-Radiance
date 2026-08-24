"""Which mod sheets are the base game's art under another name, so vanilla's labels cover them.

    python tools/labelops/vanillacopies.py            report
    python tools/labelops/vanillacopies.py --write    write vanilla-copies.json for the labeller

Two ways a mod ends up drawing the base game's pixels, and only one of them is already handled.

A recolour patches its picture into Maps/spring_town, so the game draws a sheet CALLED
spring_town and the label file of that name serves it. Nothing to do: label the base game's
sheet once and every pack that repaints it is covered, which is what the vanilla mark means.

The other way is a mod that copies vanilla tiles into a sheet of its OWN name. Nothing connects
those to the base game - the sheet is marked as the mod's own invention and its tiles read as
work nobody has done - when in truth every answer is already painted on a vanilla sheet. This
finds them, by comparing the pixels rather than the names, and says which vanilla sheet each one
is borrowing from and how much of it.

It reports rather than copies. twinlabels.py --apply is what actually writes a borrowed label,
tile by tile with its own guards; this is the map of where the borrowing is worth doing, and the
answer to "is this mod really new art or is it the base game rearranged".
"""
import argparse, base64, collections, glob, hashlib, io, json, os, sys

import numpy
from PIL import Image

from modsheets import VANILLA_SHEET_NAMES  # one rule, so the three tools cannot disagree

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
LABELER = os.environ.get("HF_LABELER_DIR") or \
          r"E:\Games\GamesMods\DevStardew\SDV-HeightFramework\tools\labeler"
TILE = 16
CELL_SHEET_STRIDE = 0x100000
ALPHA_FLOOR = 8
SKIP_SHEET = ("shadow", "darkness", "mask", "lighting", "_ore", "colors", "sky")


def is_filler(block):
    """A flat fill is not a picture, and matching one is not evidence of anything.

    SewerTiles tile 0 is sixteen by sixteen pixels of solid black, and 1,286 tiles across twenty
    mod sheets are exactly that - so it came out as the single highest-payoff tile in the corpus,
    worth more than every real tile put together. It is filler. The labeller's own twin index has
    always refused a group spanning more than twelve sheets for this reason; the root cause is
    simpler than the symptom, so this refuses the flat fill itself.
    """
    visible = block[block[:, :, 3] > ALPHA_FLOOR]
    if visible.size == 0:
        return True
    return len(numpy.unique(visible.reshape(-1, visible.shape[-1]), axis=0)) < 3


def tile_key(block):
    """The same identity twinlabels and the labeller use: colour under invisible pixels is
    whatever the exporter left there, so it is normalised away before hashing."""
    normalised = block.copy()
    normalised[block[:, :, 3] == 0] = 0
    return hashlib.sha1(normalised.tobytes()).hexdigest()


class Sheets:
    CACHED = 48

    def __init__(self, index):
        self.index = index
        self.cache = collections.OrderedDict()

    def blocks(self, sheet, relative_path):
        """(tile index -> key) for every tile of a sheet that has any pixels."""
        if relative_path in self.cache:
            self.cache.move_to_end(relative_path)
            return self.cache[relative_path]
        out = {}
        try:
            image = numpy.array(Image.open(os.path.join(HFDIR, relative_path)).convert("RGBA"))
        except Exception:
            image = None
        if image is not None:
            width = (self.index.get("artDim", {}).get(sheet) or [image.shape[1]])[0]
            columns = max(1, width // TILE)
            rows = image.shape[0] // TILE
            for row in range(rows):
                for column in range(columns):
                    block = image[row * TILE:(row + 1) * TILE, column * TILE:(column + 1) * TILE]
                    if block.shape[:2] != (TILE, TILE) or not (block[:, :, 3] > ALPHA_FLOOR).any():
                        continue
                    if is_filler(block):
                        continue
                    out[row * columns + column] = tile_key(block)
        self.cache[relative_path] = out
        while len(self.cache) > self.CACHED:
            self.cache.popitem(last=False)
        return out


def load_painted():
    """sheet -> the tiles that carry any label today."""
    import base64, glob
    out = {}
    for folder in (HFDIR, os.path.join(HFDIR, "labels")):
        for path in glob.glob(os.path.join(folder, "*.labels.json")):
            sheet = os.path.basename(path)[:-len(".labels.json")]
            if sheet in out:
                continue
            try:
                with io.open(path, encoding="utf-8") as handle:
                    document = json.load(handle)
            except ValueError:
                continue
            out[sheet] = {int(tile) for tile, blob in (document.get("tiles") or {}).items()
                          if any(base64.b64decode(blob))}
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    with io.open(os.path.join(HFDIR, "maps.json"), encoding="utf-8") as handle:
        index = json.load(handle)
    art_png = index.get("artPng") or {}
    source_of = index.get("artSrc") or {}
    sheets = Sheets(index)

    # tiles a map actually places, per sheet: borrowing a label for a tile nobody draws is work
    # that changes no pixel in the game.
    placed = collections.defaultdict(set)
    for entry in index["locations"].values():
        names = entry.get("sheets") or []
        for cell in (entry.get("used") or []):
            if cell < 0:
                continue
            sheet_index, tile = divmod(cell, CELL_SHEET_STRIDE)
            if sheet_index < len(names):
                placed[names[sheet_index]].add(tile)

    # what the base game's own sheets look like, tile by tile
    vanilla_tiles = {}
    for sheet, art in art_png.items():
        if sheet.lower() not in VANILLA_SHEET_NAMES:
            continue
        if not (source_of.get(sheet) or "").replace("\\", "/").lower().startswith("maps/"):
            continue
        for tile, key in sheets.blocks(sheet, art).items():
            vanilla_tiles.setdefault(key, (sheet, tile))
    print("vanilla tiles indexed:", len(vanilla_tiles))

    # and which mod-named sheets are made of them
    findings = []
    for sheet, art in sorted(art_png.items()):
        if sheet.lower() in VANILLA_SHEET_NAMES or not art:
            continue
        if any(word in sheet.lower() for word in SKIP_SHEET):
            continue
        drawn = placed.get(sheet)
        if not drawn:
            continue
        blocks = sheets.blocks(sheet, art)
        mine = {tile: key for tile, key in blocks.items() if tile in drawn}
        if not mine:
            continue
        borrowed = {tile: vanilla_tiles[key] for tile, key in mine.items() if key in vanilla_tiles}
        if not borrowed:
            continue
        from_sheets = collections.Counter(name for name, _ in borrowed.values())
        findings.append({
            "sheet": sheet,
            "placedTiles": len(mine),
            "fromVanilla": len(borrowed),
            "share": round(len(borrowed) / len(mine), 3),
            "mostlyFrom": from_sheets.most_common(3),
            "tiles": {str(t): "%s#%d" % v for t, v in sorted(borrowed.items())},
        })

    findings.sort(key=lambda f: (-f["fromVanilla"], f["sheet"]))
    total = sum(f["fromVanilla"] for f in findings)
    whole = [f for f in findings if f["share"] >= 0.99]
    print("mod-named sheets drawing base-game tiles:", len(findings))
    print("  placed tiles whose answer is already on a vanilla sheet:", total)
    print("  sheets that are ENTIRELY the base game's art under another name:", len(whole))
    print()
    print("worth borrowing from vanilla first:")
    for finding in findings[:15]:
        names = ", ".join("%s(%d)" % (n, c) for n, c in finding["mostlyFrom"])
        print("  %5d/%-5d %3d%%  %-40s <- %s"
              % (finding["fromVanilla"], finding["placedTiles"], finding["share"] * 100,
                 finding["sheet"][:40], names))

    if arguments.write:
        # WHAT THE SIDEBAR NEEDS, which is the other direction: not "where did this mod get its
        # art" but "what does painting this vanilla tile finish". Same facts inverted, counted
        # only over mod tiles that are not painted yet, because a tile already done unlocks
        # nothing. This is the queue the whole vanilla-first argument rests on, and until it is
        # on the row nobody painting can see it.
        painted = load_painted()
        unlocks_tile = collections.defaultdict(collections.Counter)
        # How many DIFFERENT mod sheets want each vanilla tile. One tile wanted by twenty
        # unrelated sheets is furniture everybody happens to own, not art one mod borrowed.
        spread = collections.defaultdict(set)
        for finding in findings:
            mine = painted.get(finding["sheet"], set())
            for tile, source in finding["tiles"].items():
                if int(tile) in mine:
                    continue
                sheet, _, number = source.rpartition("#")
                if int(number) in painted.get(sheet, set()):
                    continue                        # already answered; twinlabels can take it
                unlocks_tile[sheet][int(number)] += 1
                spread[(sheet, int(number))].add(finding["sheet"])
        SPREAD_TOO_WIDE = 12
        for sheet, counts in unlocks_tile.items():
            for tile in [t for t, n in counts.items() if len(spread[(sheet, t)]) > SPREAD_TOO_WIDE]:
                del counts[tile]
        unlocks_tile = {s: c for s, c in unlocks_tile.items() if c}
        unlocks = {sheet: {"total": sum(counts.values()),
                           "tiles": {str(t): n for t, n in counts.most_common()}}
                   for sheet, counts in unlocks_tile.items()}
        borrows = {f["sheet"]: {"share": f["share"], "fromVanilla": f["fromVanilla"],
                                "placedTiles": f["placedTiles"],
                                "mostlyFrom": [n for n, _ in f["mostlyFrom"]]}
                   for f in findings}
        payload = {"//": "Mod-named sheets whose placed tiles are pixel-identical to a base-game "
                         "tile. `unlocks` is the same fact from the other side: how much mod work "
                         "painting each vanilla tile would finish. Written by vanillacopies.py.",
                   "sheets": findings, "unlocks": unlocks, "borrows": borrows}
        with io.open(os.path.join(HFDIR, "vanilla-copies.json"), "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False)
        if os.path.isdir(LABELER):
            with io.open(os.path.join(LABELER, "vanillacopies.js"), "w", encoding="utf-8") as handle:
                handle.write("// Mod sheets that are the base game's art under another name, and\n"
                             "// what painting each base-game tile would finish. vanillacopies.py.\n"
                             "window.VANILLACOPIES=")
                json.dump(payload, handle, ensure_ascii=False)
                handle.write(";\n")
        best = sorted(unlocks.items(), key=lambda kv: -kv[1]["total"])[:8]
        print()
        print("painting these base-game sheets finishes this much mod work:")
        for sheet, entry in best:
            print("  %5d mod tiles  <-  %-34s (%d of its tiles are wanted)"
                  % (entry["total"], sheet, len(entry["tiles"])))
        print("\nwrote vanilla-copies.json")


if __name__ == "__main__":
    main()
