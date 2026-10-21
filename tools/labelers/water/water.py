"""The water labels tool: check and paint the liquid on every tilesheet, one sheet at a time.

    python tools/labelers/water/water.py [--store E:\\Games\\GamesMods\\DevStardew\\_label-store] [--port 8766]

then open http://127.0.0.1:8766. It is the first job of the split label tools (plan:
docs/local/project/2026-10-17-label-tools-split-plan.local.md), built on tools/labelkit.

What it reads, all of it left as it is:
  labels/water-labels.json       the shipped labels, 15 classes a pixel (HF Studio's work)
  labels/sheet-sources.json      which mod's pack ships each sheet, for grouping by mod
  Documents/HF-Studio/maps.json  the map dump: each sheet's picture, and the tiles the game marks
                                 as water (Back layer "Water"), which is what finds missing labels

What it writes: one file per sheet under <store>/water/<sheet>.json, the tiles changed here. They
are laid over water-labels.json by tools/labelops/wateroverlay.py (run by tools/sync-labels.ps1),
and only for liquid: a pixel the overlay or the shipped label calls liquid takes the overlay's
value, every other pixel keeps the shipped one. So HF Studio stays the owner of walls, roofs and
the rest, and the two tools never fight over the same pixel.

A tile's status here:
  missing   the game says water and no liquid is labelled: what most needs a look
  labelled  liquid labelled already and not looked at here
  draft     painted here, not yet confirmed (still to check)
  checked   confirmed as it was
  edited    confirmed after painting
"""
import argparse, base64, datetime, json, os, sys, threading

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, os.path.join(REPO, "tools", "labelkit"))
from labelkit_server import Events, Reply, error, serve, write_json_atomic  # noqa: E402

LABELS = os.path.join(REPO, "labels", "water-labels.json")
SOURCES = os.path.join(REPO, "labels", "sheet-sources.json")
STUDIO = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio")
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]
LIQUID = {1, 9, 10, 11, 14}      # water, ice, flowing, lava, hot
STATUSES = ("draft", "checked", "edited")
GAME = "the game"


def has_liquid(cells):
    return any(value in LIQUID for value in cells)


class Water:
    def __init__(self, store):
        self.store = os.path.join(store, "water")
        os.makedirs(self.store, exist_ok=True)
        self.lock = threading.Lock()
        with open(LABELS, encoding="utf-8") as file:
            shipped = json.load(file)["sheets"]
        self.base = {name: {int(tile): base64.b64decode(cells) for tile, cells in sheet["tiles"].items()}
                     for name, sheet in shipped.items()}
        self.label_size = {name: sheet.get("size") for name, sheet in shipped.items()}
        with open(os.path.join(STUDIO, "maps.json"), encoding="utf-8") as file:
            dump = json.load(file)
        self.art = dump.get("artPng", {})
        self.dimensions = dump.get("artDim", {})
        self.art_source = dump.get("artSrc", {})
        self.game_water = {name: set(tiles) for name, tiles in dump.get("water", {}).items()}
        # every place the dump saw, by the sheets it draws with, for the map view
        self.places = {key: {"sheets": place.get("sheets", []), "file": place.get("file"), "outdoors": place.get("outdoors")}
                       for key, place in dump.get("locations", {}).items() if place.get("file")}
        with open(SOURCES, encoding="utf-8") as file:
            sources = json.load(file)
        self.mod_names = sources.get("mods", {})
        self.sources = sources.get("sheets", {})
        self.overlay = {}
        for entry in os.listdir(self.store):
            if entry.endswith(".json"):
                with open(os.path.join(self.store, entry), encoding="utf-8") as file:
                    record = json.load(file)
                self.overlay[record["sheet"]] = {int(tile): {"cells": base64.b64decode(value["classes"]), "status": value["status"],
                                                             "when": value.get("when")}
                                                 for tile, value in record["tiles"].items()}
        liquid_sheets = {name for name, tiles in self.base.items() if any(has_liquid(cells) for cells in tiles.values())}
        wanted = liquid_sheets | set(self.game_water) | set(self.overlay)
        self.no_picture = sorted(name for name in wanted if name not in self.art)
        self.sheets = sorted(name for name in wanted if name in self.art)

    # ---- one sheet ----
    def size(self, name):
        width, height = self.dimensions.get(name) or self.label_size.get(name) or (16, 16)
        return int(width), int(height)

    def cells(self, name, tile):
        """The tile as this tool sees it: its own paint if any, else the shipped label."""
        changed = self.overlay.get(name, {}).get(tile)
        if changed:
            return changed["cells"]
        return self.base.get(name, {}).get(tile)

    def tile_status(self, name, tile):
        changed = self.overlay.get(name, {}).get(tile)
        if changed:
            return changed["status"]
        cells = self.base.get(name, {}).get(tile)
        if cells is not None and has_liquid(cells):
            return "labelled"
        if tile in self.game_water.get(name, ()):
            return "missing"
        return None

    def tiles_of(self, name):
        tiles = set(self.game_water.get(name, ())) | set(self.overlay.get(name, {}))
        tiles |= {tile for tile, cells in self.base.get(name, {}).items() if has_liquid(cells)}
        return sorted(tiles)

    def summary(self, name):
        counts = {"missing": 0, "labelled": 0, "draft": 0, "checked": 0, "edited": 0}
        for tile in self.tiles_of(name):
            status = self.tile_status(name, tile)
            if status:
                counts[status] += 1
        width, height = self.size(name)
        return {"name": name, "width": width, "height": height, "counts": counts}

    def mod_of(self, name):
        """The pack a sheet is grouped under. A recolour that repaints a base game sheet does not own
        it: the sheet stays with the game. A sheet nobody labelled yet is not in sheet-sources.json,
        so it is placed by where the map dump found its picture."""
        source = self.sources.get(name)
        if source:
            if source.get("from") == "a mod" and source.get("mods"):
                return sorted(source["mods"])[0]
            if source.get("from") in ("the base game", "a mod, over a base game name"):
                return GAME
        path = (self.art_source.get(name) or "").replace("\\", "/")
        if path.startswith("Maps/"):
            return GAME
        if path.startswith("SMAPI/"):
            return path.split("/")[1]
        parts = path.split("/")
        for index, part in enumerate(parts[:-3]):
            if part.lower().startswith("mods"):
                # <Mods folder>/<the library's own category>/<the mod's folder>/...
                return "folder:" + parts[index + 2]
        return "unknown"

    def mod_display(self, mod):
        if mod == GAME:
            return "Stardew Valley"
        if mod == "unknown":
            return "Unknown pack"
        if mod.startswith("folder:"):
            return mod[7:]
        return self.mod_names.get(mod, mod)

    # ---- routes ----
    def mods(self, query):
        groups = {}
        for name in self.sheets:
            groups.setdefault(self.mod_of(name), []).append(name)
        out = []
        for mod, names in groups.items():
            totals = {"missing": 0, "labelled": 0, "draft": 0, "checked": 0, "edited": 0}
            for name in names:
                for key, value in self.summary(name)["counts"].items():
                    totals[key] += value
            out.append({"id": mod, "name": self.mod_display(mod), "sheets": len(names), "counts": totals})
        out.sort(key=lambda row: (row["id"] != GAME, row["name"].lower()))
        return {"mods": out, "noPicture": self.no_picture}

    def sheets_of(self, query):
        mod = query.get("mod", [""])[0]
        names = [name for name in self.sheets if self.mod_of(name) == mod]
        return [self.summary(name) for name in names]

    def sheet(self, query):
        name = query.get("name", [""])[0]
        if name not in self.art:
            return error("no picture for this sheet", 404)
        width, height = self.size(name)
        columns = max(1, width // 16)
        full = bytearray(width * height)
        known = set(self.base.get(name, {})) | set(self.overlay.get(name, {}))
        for tile in known:
            cells = self.cells(name, tile)
            column, row = tile % columns, tile // columns
            if cells is None or row * 16 >= height:
                continue
            for y in range(16):
                start = (row * 16 + y) * width + column * 16
                full[start:start + 16] = cells[y * 16:y * 16 + 16]
        tiles = {tile: self.tile_status(name, tile) for tile in self.tiles_of(name)}
        shipped = {tile: base64.b64encode(cells).decode() for tile, cells in self.base.get(name, {}).items() if tile in tiles}
        return {"name": name, "width": width, "height": height, "columns": columns, "mod": self.mod_of(name),
                "classes": base64.b64encode(bytes(full)).decode(), "tiles": tiles,
                "gameWater": sorted(self.game_water.get(name, ())), "shipped": shipped}

    def image(self, query):
        name = query.get("name", [""])[0]
        path = os.path.join(STUDIO, self.art.get(name, ""))
        if name not in self.art or not os.path.isfile(path):
            return error("no picture", 404)
        with open(path, "rb") as file:
            return Reply(file.read(), "image/png", cache=True)

    # ---- the map view: a place drawn from its own tiles, with the liquid laid over it ----
    def places_of(self, query):
        """Every place that draws with this sheet, once per name: a place has a variant per set of
        mods that changed it, and the plain one (no ~) is shown when there is one."""
        sheet = query.get("sheet", [""])[0]
        chosen = {}
        for key, place in self.places.items():
            if sheet not in place["sheets"]:
                continue
            name = key.split("~")[0]
            if name not in chosen or "~" in chosen[name]:
                chosen[name] = key
        return sorted(({"key": key, "name": name, "outdoors": self.places[key]["outdoors"]} for name, key in chosen.items()),
                      key=lambda row: row["name"].lower())

    def place(self, query):
        key = query.get("key", [""])[0]
        if key not in self.places:
            return error("unknown place", 404)
        with open(os.path.join(STUDIO, self.places[key]["file"]), encoding="utf-8") as file:
            data = json.load(file)
        sheets = data.get("sheets", [])
        used = [set() for _ in sheets]
        for layer in data.get("layers", []):
            cells = base64.b64decode(layer["cells"])
            for k in range(0, len(cells), 4):
                value = int.from_bytes(cells[k:k + 4], "little", signed=True)
                if value >= 0 and value // 0x100000 < len(sheets):
                    used[value // 0x100000].add(value % 0x100000)
        liquid = {}
        for number, name in enumerate(sheets):
            mine = {}
            for tile in used[number]:
                cells = self.cells(name, tile)
                if cells is not None and has_liquid(cells):
                    mine[tile] = base64.b64encode(cells).decode()
            liquid[name] = mine
        return {"key": key, "name": key.split("~")[0], "sheets": sheets, "sheetArt": data.get("sheetArt", []),
                "sheetWH": data.get("sheetWH", []), "layers": [{"id": layer["id"], "ord": layer.get("ord", 0), "fam": layer.get("fam"),
                                                                "w": layer["w"], "h": layer["h"], "cells": layer["cells"]}
                                                               for layer in data.get("layers", [])],
                "waterGrid": data.get("wgrid"), "liquid": liquid}

    def studio_picture(self, query):
        """A sheet picture from the map dump, by the path the dump wrote; only from its sheets folder."""
        path = query.get("path", [""])[0].replace("\\", "/")
        name = os.path.basename(path)
        full = os.path.join(STUDIO, "sheets", name)
        if not path.startswith("sheets/") or not name.endswith(".png") or not os.path.isfile(full):
            return error("no picture", 404)
        with open(full, "rb") as file:
            return Reply(file.read(), "image/png", cache=True)

    def save_tiles(self, body):
        """{sheet, tiles: {index: {classes: base64 of 256, status} | {remove: true}}}"""
        name = body.get("sheet")
        if name not in self.art:
            return error("unknown sheet", 404)
        stamp = datetime.datetime.now().isoformat(timespec="seconds")
        published = {}
        with self.lock:
            changed = self.overlay.setdefault(name, {})
            for key, value in (body.get("tiles") or {}).items():
                tile = int(key)
                if value.get("remove"):
                    changed.pop(tile, None)
                    published[tile] = {"status": self.tile_status(name, tile), "classes": None}
                    continue
                status = value.get("status")
                cells = base64.b64decode(value.get("classes") or "")
                if status not in STATUSES or len(cells) != 256:
                    return error(f"tile {tile}: bad status or classes")
                changed[tile] = {"cells": cells, "status": status, "when": stamp}
                published[tile] = {"status": status, "classes": value["classes"]}
            record = {"sheet": name, "size": list(self.size(name)), "format": "16x16-classes-base64", "classes": CLASSES,
                      "tiles": {str(tile): {"classes": base64.b64encode(entry["cells"]).decode(), "status": entry["status"], "when": entry["when"]}
                                for tile, entry in sorted(changed.items())}}
            write_json_atomic(os.path.join(self.store, file_name(name) + ".json"), record)
        events.publish(kind="tiles", sheet=name, mod=self.mod_of(name), tiles=published, summary=self.summary(name))
        return {"ok": True, "summary": self.summary(name)}


def file_name(sheet):
    """A sheet name as a file name: the characters Windows refuses become _."""
    return "".join("_" if ch in '<>:"/\\|?*' else ch for ch in sheet)


events = Events()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--store", default=os.path.join(os.path.dirname(REPO), "_label-store"))
    parser.add_argument("--port", type=int, default=8766)
    args = parser.parse_args()
    water = Water(args.store)
    missing = sum(water.summary(name)["counts"]["missing"] for name in water.sheets)
    print(f"{len(water.sheets)} sheets with liquid or game water ({missing} tiles the game calls water with no liquid label); "
          f"{len(water.no_picture)} without a picture; changes in {water.store}", flush=True)
    serve(page=os.path.join(HERE, "water.html"),
          get_routes={"/api/mods": water.mods, "/api/sheets": water.sheets_of, "/api/sheet": water.sheet, "/api/image": water.image,
                      "/api/places": water.places_of, "/api/place": water.place, "/api/studio": water.studio_picture},
          post_routes={"/api/tiles": water.save_tiles},
          events=events, port=args.port, title="Water labels")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
