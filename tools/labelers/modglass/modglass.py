"""The mod glass tool: put the window glass right on one Content Patcher pack that repaints buildings.

    python tools/labelers/modglass/modglass.py --mod "<the pack's [CP] folder>" [--palette Elle] [--season summer] [--port 8767]

then open http://127.0.0.1:8767. Made for Elle's Town Buildings (Nexus 14524), whose theater door
is narrower than the base game's, so the shipped glass label lay over its wooden frame and the
wall beside it. It reads any pack the same way.

What it reads, all of it left as it is:
  <pack>/content.json            every EditImage on a Maps/ sheet the shipped labels know
  labels/water-labels.json       the shipped labels, 15 classes a pixel
  Documents/HF-Studio/maps.json  the base game's own picture of each sheet (artPng)

A building is one of the pack's own config switches ("Movie Theatre", "Bathhouse"), so the list
reads the way the pack's settings do. Its picture is the base game's sheet with that building's
patches laid over it, in the pack's order and with its Replace and Overlay modes. Only the tiles
where the pack's picture differs from the base game's are listed:
  glass     the shipped label has glass or liquid there and the art under it changed: most need a look
  changed   the art changed and the shipped label has no glass there: a new window may need some
  draft     painted here, not yet confirmed (still to check)
  checked   confirmed as the shipped label had it
  edited    confirmed after painting

What it writes: one file per building and sheet under <store>/<palette>/, the tiles changed here.
Export (the button, or POST /api/export) gathers the confirmed ones into one labels file for
tools/labelops/makevariant.py, the same tile on every season's sheet the building's patches cover,
and fingerprint passes of the pack's art for every palette and season, so the mod uses these
labels where that art is really loaded and the shipped ones everywhere else.
"""
import argparse, base64, datetime, io, json, os, re, sys, threading

import numpy
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, os.path.join(REPO, "tools", "labelkit"))
sys.path.insert(0, os.path.join(REPO, "tools", "labelops"))
from labelkit_server import Events, Reply, error, serve, write_json_atomic  # noqa: E402
from artfingerprints import alpha_readings  # noqa: E402

LABELS = os.path.join(REPO, "labels", "water-labels.json")
STUDIO = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio")
PASSES = os.path.join(STUDIO, "fingerprints")
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]
GLASSY = {8, 12, 13}             # mirror, window, glass: what the reflection in windows reads
LIQUID = {1, 9, 10, 11, 14}      # water, ice, flowing, lava, hot water: a pack can move a pool too
STATUSES = ("draft", "checked", "edited")
SEASONS = ("spring", "summer", "fall", "winter")
# When keys that are the game's state rather than one of the pack's switches
GAME_CONDITIONS = {"season", "palette", "iscommunitycentercomplete", "isjojamartcomplete", "hasflag", "hasmod", "weather"}


def has_glass(cells):
    return any(value in GLASSY for value in cells)


def needs_look(cells):
    """Glass or liquid: the labels a changed picture most often leaves in the wrong place."""
    return any(value in GLASSY or value in LIQUID for value in cells)


def read_content(folder):
    """content.json, with the comments and trailing commas Content Patcher allows taken out."""
    with open(os.path.join(folder, "content.json"), encoding="utf-8-sig") as file:
        text = file.read()
    text = re.sub(r"(?m)^\s*//.*$", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return json.loads(text)


def listed(value):
    return [part.strip().lower() for part in str(value).split(",") if part.strip()]


class Pack:
    """The pack's patches, sorted into buildings, for one palette."""

    def __init__(self, folder, palette):
        self.folder, self.palette = folder, palette
        content = read_content(folder)
        manifest_path = os.path.join(folder, "manifest.json")
        with open(manifest_path, encoding="utf-8-sig") as file:
            self.manifest = json.loads(re.sub(r",(\s*[}\]])", r"\1", file.read()))
        self.config = {key: str(entry.get("Default", "")) for key, entry in (content.get("ConfigSchema") or {}).items()}
        self.palettes = [value.strip() for value in str((content.get("ConfigSchema") or {}).get("Palette", {}).get("AllowValues", palette)).split(",")]
        self.patches = [change for change in content.get("Changes", []) if change.get("Action") == "EditImage"
                        and str(change.get("Target", "")).startswith("Maps/")]

    def building_of(self, patch):
        """The pack's own switch that turns this patch on, else its log name without the number."""
        for key in (patch.get("When") or {}):
            if key.lower() not in GAME_CONDITIONS and key in self.config and key != "Map Buildings":
                return key
        return re.sub(r"\s*\d+$", "", patch.get("LogName", "?"))

    def applies(self, patch, season, palette):
        for key, wanted in (patch.get("When") or {}).items():
            low = key.lower()
            if low == "season":
                if season not in listed(wanted):
                    return False
            elif low == "palette":
                if palette.lower() not in listed(wanted):
                    return False
            elif key in self.config:
                if self.config[key].lower() not in listed(wanted):
                    return False
            # the game's own state (which route was taken, flags): the building is shown either way
        return True

    def fill(self, text, season, palette):
        return text.replace("{{season}}", season).replace("{{Season}}", season.capitalize()).replace("{{Palette}}", palette)

    def patches_for(self, sheet_name, season, palette, building=None):
        out = []
        for patch in self.patches:
            if self.fill(patch["Target"], season, palette)[5:] != sheet_name:
                continue
            if building and self.building_of(patch) != building:
                continue
            if self.applies(patch, season, palette):
                out.append(patch)
        return out

    def paint(self, base, sheet_name, season, palette, building=None):
        """The base game's sheet with the pack's patches laid over it, as Content Patcher would."""
        picture = base.copy()
        for patch in self.patches_for(sheet_name, season, palette, building):
            path = os.path.join(self.folder, self.fill(patch["FromFile"], season, palette))
            if not os.path.isfile(path):
                continue
            source = Image.open(path).convert("RGBA")
            area = patch.get("FromArea") or {"X": 0, "Y": 0, "Width": source.width, "Height": source.height}
            to = patch.get("ToArea") or {"X": 0, "Y": 0, "Width": area["Width"], "Height": area["Height"]}
            piece = source.crop((area["X"], area["Y"], area["X"] + area["Width"], area["Y"] + area["Height"]))
            if (piece.width, piece.height) != (to["Width"], to["Height"]):
                piece = piece.resize((to["Width"], to["Height"]), Image.NEAREST)
            if str(patch.get("PatchMode", "Replace")).lower() == "overlay":
                under = picture.crop((to["X"], to["Y"], to["X"] + to["Width"], to["Y"] + to["Height"]))
                under.alpha_composite(piece)
                piece = under
            picture.paste(piece, (to["X"], to["Y"]))
        return picture


class ModGlass:
    def __init__(self, pack, season, store):
        self.pack, self.season = pack, season
        self.store = store
        os.makedirs(self.store, exist_ok=True)
        self.lock = threading.Lock()
        with open(LABELS, encoding="utf-8") as file:
            shipped = json.load(file)["sheets"]
        self.base = {name: {int(tile): base64.b64decode(cells) for tile, cells in sheet["tiles"].items()}
                     for name, sheet in shipped.items()}
        with open(os.path.join(STUDIO, "maps.json"), encoding="utf-8") as file:
            self.art = json.load(file).get("artPng", {})
        self.vanilla_cache = {}
        self.picture_cache = {}
        self.items = {}            # id -> {building, sheet, tiles: {tile: kind}}
        self.no_picture = set()
        self.build_items()
        self.overlay = {}          # id -> {tile: {cells, status, when}}
        for entry in os.listdir(self.store):
            if entry.endswith(".json") and not entry.startswith("export"):
                with open(os.path.join(self.store, entry), encoding="utf-8") as file:
                    record = json.load(file)
                if record.get("id") in self.items:
                    self.overlay[record["id"]] = {int(tile): {"cells": base64.b64decode(value["classes"]), "status": value["status"],
                                                              "when": value.get("when")}
                                                  for tile, value in record["tiles"].items()}

    # ---- pictures ----
    def vanilla(self, sheet):
        if sheet not in self.vanilla_cache:
            path = os.path.join(STUDIO, self.art.get(sheet, ""))
            self.vanilla_cache[sheet] = Image.open(path).convert("RGBA") if sheet in self.art and os.path.isfile(path) else None
        return self.vanilla_cache[sheet]

    def picture(self, item_id, palette=None, season=None):
        item = self.items[item_id]
        palette, season = palette or self.pack.palette, season or self.season
        sheet = seasonal(item["sheet"], season)
        key = (item_id, palette, season)
        if key not in self.picture_cache:
            base = self.vanilla(sheet)
            self.picture_cache[key] = None if base is None else self.pack.paint(base, sheet, season, palette, item["building"])
        return self.picture_cache[key]

    def build_items(self):
        sheets = {}
        for patch in self.pack.patches:
            if not self.pack.applies(patch, self.season, self.pack.palette):
                continue
            sheet = self.pack.fill(patch["Target"], self.season, self.pack.palette)[5:]
            if sheet not in self.base:
                continue
            sheets.setdefault((self.pack.building_of(patch), sheet), True)
        for building, sheet in sheets:
            item_id = f"{building} · {sheet}"
            base = self.vanilla(sheet)
            if base is None:
                self.no_picture.add(sheet)
                continue
            self.items[item_id] = {"building": building, "sheet": sheet, "tiles": {}}
            painted = numpy.array(self.picture(item_id))
            plain = numpy.array(base)
            columns = plain.shape[1] // 16
            for row in range(plain.shape[0] // 16):
                for column in range(columns):
                    a = painted[row * 16:row * 16 + 16, column * 16:column * 16 + 16]
                    b = plain[row * 16:row * 16 + 16, column * 16:column * 16 + 16]
                    if numpy.array_equal(a, b):
                        continue
                    tile = row * columns + column
                    cells = self.base[sheet].get(tile)
                    self.items[item_id]["tiles"][tile] = "glass" if cells is not None and needs_look(cells) else "changed"
            if not self.items[item_id]["tiles"]:
                del self.items[item_id]

    # ---- one item ----
    def cells(self, item_id, tile):
        changed = self.overlay.get(item_id, {}).get(tile)
        if changed:
            return changed["cells"]
        return self.base[self.items[item_id]["sheet"]].get(tile)

    def tile_status(self, item_id, tile):
        changed = self.overlay.get(item_id, {}).get(tile)
        return changed["status"] if changed else self.items[item_id]["tiles"].get(tile)

    def summary(self, item_id):
        counts = {"glass": 0, "changed": 0, "draft": 0, "checked": 0, "edited": 0}
        for tile in self.items[item_id]["tiles"]:
            counts[self.tile_status(item_id, tile)] += 1
        item = self.items[item_id]
        return {"id": item_id, "building": item["building"], "sheet": item["sheet"], "counts": counts}

    # ---- routes ----
    def list_items(self, query):
        rows = [self.summary(item_id) for item_id in self.items]
        rows.sort(key=lambda row: (-(row["counts"]["glass"] + row["counts"]["draft"]), row["building"].lower(), row["sheet"]))
        return {"pack": self.pack.manifest.get("Name", "the pack"), "palette": self.pack.palette, "palettes": self.pack.palettes,
                "season": self.season, "items": rows, "noPicture": sorted(self.no_picture)}

    def item(self, query):
        item_id = query.get("id", [""])[0]
        if item_id not in self.items:
            return error("unknown building", 404)
        item = self.items[item_id]
        sheet = item["sheet"]
        width, height = self.vanilla(sheet).size
        columns = width // 16
        full = bytearray(width * height)
        for tile in set(self.base[sheet]) | set(self.overlay.get(item_id, {})):
            cells = self.cells(item_id, tile)
            column, row = tile % columns, tile // columns
            if cells is None or row * 16 >= height:
                continue
            for y in range(16):
                start = (row * 16 + y) * width + column * 16
                full[start:start + 16] = cells[y * 16:y * 16 + 16]
        tiles = {tile: self.tile_status(item_id, tile) for tile in item["tiles"]}
        shipped = {tile: base64.b64encode(self.base[sheet][tile]).decode() for tile in item["tiles"] if tile in self.base[sheet]}
        return {"id": item_id, "building": item["building"], "sheet": sheet, "width": width, "height": height, "columns": columns,
                "classes": base64.b64encode(bytes(full)).decode(), "tiles": tiles, "shipped": shipped}

    def image(self, query):
        item_id = query.get("id", [""])[0]
        which = query.get("art", ["pack"])[0]
        if item_id not in self.items:
            return error("unknown building", 404)
        picture = self.vanilla(self.items[item_id]["sheet"]) if which == "base" else self.picture(item_id)
        buffer = io.BytesIO()
        picture.save(buffer, "PNG")
        return Reply(buffer.getvalue(), "image/png", cache=True)

    def save_tiles(self, body):
        """{id, tiles: {index: {classes: base64 of 256, status} | {remove: true}}}"""
        item_id = body.get("id")
        if item_id not in self.items:
            return error("unknown building", 404)
        stamp = datetime.datetime.now().isoformat(timespec="seconds")
        published = {}
        with self.lock:
            changed = self.overlay.setdefault(item_id, {})
            for key, value in (body.get("tiles") or {}).items():
                tile = int(key)
                if tile not in self.items[item_id]["tiles"]:
                    return error(f"tile {tile} is not one this building changes")
                if value.get("remove"):
                    changed.pop(tile, None)
                    published[tile] = {"status": self.tile_status(item_id, tile), "classes": None}
                    continue
                status = value.get("status")
                cells = base64.b64decode(value.get("classes") or "")
                if status not in STATUSES or len(cells) != 256:
                    return error(f"tile {tile}: bad status or classes")
                changed[tile] = {"cells": cells, "status": status, "when": stamp}
                published[tile] = {"status": status, "classes": value["classes"]}
            item = self.items[item_id]
            record = {"id": item_id, "building": item["building"], "sheet": item["sheet"], "palette": self.pack.palette,
                      "season": self.season, "format": "16x16-classes-base64", "classes": CLASSES,
                      "tiles": {str(tile): {"classes": base64.b64encode(entry["cells"]).decode(), "status": entry["status"], "when": entry["when"]}
                                for tile, entry in sorted(changed.items())}}
            write_json_atomic(os.path.join(self.store, file_name(item_id) + ".json"), record)
        events.publish(kind="tiles", id=item_id, tiles=published, summary=self.summary(item_id))
        return {"ok": True, "summary": self.summary(item_id)}

    def export(self, body):
        """The confirmed labels for makevariant.py, and fingerprint passes of the pack's art.

        A label painted on this season's sheet goes onto every season's sheet the building's
        patches also cover; the passes say which art each copy belongs to, so a season whose
        drawing differs simply never matches it and keeps the shipped label."""
        slug = re.sub(r"[^a-z0-9]+", "-", self.pack.manifest.get("Name", "pack").lower()).strip("-")
        labels, conflicts = {}, []
        for item_id, tiles in self.overlay.items():
            sheet = self.items[item_id]["sheet"]
            for tile, entry in tiles.items():
                if entry["status"] not in ("checked", "edited"):
                    continue
                for season in SEASONS:
                    target = seasonal(sheet, season)
                    if target != sheet and not self.pack.patches_for(target, season, self.pack.palette, self.items[item_id]["building"]):
                        continue
                    label = base64.b64encode(entry["cells"]).decode()
                    previous = labels.setdefault(target, {}).get(str(tile))
                    if previous and previous != label:
                        conflicts.append(f"{target} {tile}")
                    labels[target][str(tile)] = label
        labels_path = os.path.join(self.store, f"export-{slug}.json")
        write_json_atomic(labels_path, {"format": 1, "source": self.pack.manifest.get("Name"),
                                        "sheets": {sheet: {"tiles": tiles} for sheet, tiles in labels.items()}})
        pass_names = []
        for palette in self.pack.palettes:
            for season in SEASONS:
                for reading in (0, 1):
                    sheets_out = {}
                    for sheet, tiles in labels.items():
                        if seasonal(sheet, season) != sheet:
                            continue
                        base = self.vanilla(sheet)
                        if base is None:
                            continue
                        picture = base
                        for building in {self.items[i]["building"] for i in self.items}:
                            picture = self.pack.paint(picture, sheet, season, palette, building)
                        pixels = numpy.array(picture)
                        columns = pixels.shape[1] // 16
                        found = {}
                        for tile in tiles:
                            row, column = divmod(int(tile), columns)
                            readings = sorted(alpha_readings(pixels[row * 16:row * 16 + 16, column * 16:column * 16 + 16]))
                            if reading < len(readings):
                                found[tile] = readings[reading]
                        if found:
                            sheets_out[sheet] = {"tiles": found}
                    if not sheets_out:
                        continue
                    name = f"{slug}-{palette.lower()}-{season}" + ("" if reading == 0 else "-soft")
                    write_json_atomic(os.path.join(PASSES, name + ".json"),
                                      {"format": 1, "label": name, "takenAt": datetime.datetime.now().isoformat(timespec="seconds"),
                                       "source": "tools/labelers/modglass: the pack's art laid over the base game's, read off disk",
                                       "sheets": sheets_out})
                    pass_names.append(name)
        command = (f'python tools/labelops/makevariant.py --name {slug} --describe "{self.pack.manifest.get("Name")}" '
                   f'--labels "{labels_path}" --mods {self.pack.manifest.get("UniqueID", "")} --passes {" ".join(pass_names)}')
        count = sum(len(tiles) for tiles in labels.values())
        return {"ok": True, "tiles": count, "sheets": len(labels), "labels": labels_path, "passes": pass_names,
                "conflicts": conflicts, "command": command}


def seasonal(sheet, season):
    """summer_town -> <season>_town; a sheet with no season in its name stays itself."""
    match = re.match(r"(?i)(spring|summer|fall|winter)_(.+)", sheet)
    return f"{season}_{match.group(2)}" if match else sheet


def file_name(text):
    return "".join("_" if ch in '<>:"/\\|?*·' else ch for ch in text).replace(" ", "_")


events = Events()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mod", required=True, help="the pack's folder, the one holding content.json")
    parser.add_argument("--palette", default=None, help="the pack's Palette setting to paint on (default: the pack's own default)")
    parser.add_argument("--season", default="summer", choices=SEASONS)
    parser.add_argument("--store", default=os.path.join(os.path.dirname(REPO), "_label-store", "modglass"))
    parser.add_argument("--port", type=int, default=8767)
    args = parser.parse_args()
    probe = Pack(args.mod, args.palette or "")
    palette = args.palette or probe.config.get("Palette") or (probe.palettes[0] if probe.palettes else "")
    pack = Pack(args.mod, palette)
    slug = re.sub(r"[^a-z0-9]+", "-", pack.manifest.get("Name", "pack").lower()).strip("-")
    tool = ModGlass(pack, args.season, os.path.join(args.store, slug, palette or "default"))
    glass = sum(tool.summary(item_id)["counts"]["glass"] for item_id in tool.items)
    print(f"{pack.manifest.get('Name')}: {len(tool.items)} building sheets, {glass} glass tiles on changed art "
          f"({palette}, {args.season}); changes in {tool.store}", flush=True)
    if tool.no_picture:
        print("no base game picture for: " + ", ".join(sorted(tool.no_picture)), flush=True)
    serve(page=os.path.join(HERE, "modglass.html"),
          get_routes={"/api/items": tool.list_items, "/api/item": tool.item, "/api/image": tool.image},
          post_routes={"/api/tiles": tool.save_tiles, "/api/export": tool.export},
          events=events, port=args.port, title="Mod glass")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
