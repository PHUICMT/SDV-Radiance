"""Which mod uses which tilesheet, whose art it really is, and how much is left to label.

    python tools/labelops/modsheets.py              write modsheets.js for the labeller
    python tools/labelops/modsheets.py --report     print the summary and write nothing

Run it after a per-mod sweep (gensoloprofiles.py + run.py). It only reads, so running it during
a sweep is safe and useful for a progress report, but the answer is only about the passes already
in the dump and a run that catches maps.json mid-write simply fails and can be repeated.

THE THREE MARKS, and why they cannot be read off a name.

A recolour pack ships `fall_town_green.png` and patches it into `Maps/{{season}}_town`, so the
sheet the game draws is called `spring_town` and is not the base game's `spring_town`. Judging
by asset name calls that vanilla and labels the wrong picture; judging by the pack's own file
name misses it entirely, because that name never reaches the game. So the judgement is on the
PICTURE. radiance_mapdump writes each sheet as `<name>_<hash of path>_<hash of the png bytes>`,
and this compares the second hash against the same sheet name in the Label-BaseArt pass:

    vanilla   the base game's own art, byte for byte. Label it once under the base game and
              every mod that draws it is covered.
    repainted the base game's sheet NAME carrying different pixels: a recolour, or a Load that
              replaced the file. Needs its own labels, and its own row.
    own       a sheet the base game has no name for at all. This half IS decided by name, and
              only this half: a name that is not an asset in Content/Maps cannot be vanilla art
              whatever its pixels, and vanilla-maps.json holds all 302 of those names.
    unverified a vanilla sheet name whose vanilla pixels are nowhere in the dump. Rare, and
              counted apart rather than guessed at, because calling a mod's sheet vanilla when
              nothing was compared is the one answer that would put a wrong label on real art.

WHAT IS LEFT TO DO, per mod, is the count that decides whether a mod needs opening at all:
tiles its maps actually PLACE, minus tiles already labelled on that sheet. A mod whose count is
zero is finished even if nobody has ever opened it, which is the answer to "if a mod uses only
vanilla art and vanilla is fully labelled, is there anything to do" - yes if it places a vanilla
tile no vanilla map ever placed, no otherwise, and only this count can tell the two apart.

ONE ROW PER THING THAT IS REALLY DIFFERENT. Locations are keyed in the dump by cells AND art,
so one Town becomes many rows when several mods merely recolour it. Here the versions of a
place are grouped by their LAYOUT alone, and a group says which mods produce it and, when they
disagree about art, exactly which sheets they disagree about.
"""
import argparse, collections, glob, io, json, os, sys, base64, hashlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gensoloprofiles                    # for MUST, the tooling every profile carries

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
# Overridable so the script can be proved against an archived dump while a sweep is
# writing the live one: reading maps.json mid-sweep gets a file half rewritten.
HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
# Overridable for the same reason as HFDIR: a test run must be able to write its
# modsheets.js somewhere other than the labeller the user has open.
LABELLER = os.environ.get("HF_LABELER_DIR") or r"E:\Games\GamesMods\DevStardew\SDV-HeightFramework\tools\labeler"
BASE_PASS = "Label-BaseArt"
CELL_SHEET_STRIDE = 0x100000
TILE_PIXELS = 16
PROFILE_DIR = os.path.join(r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
                           "mod-profiles")
# Sheets that carry no surface anybody labels: shadow, darkness and mask overlays, lighting and
# ore layers, palette strips and skyboxes. Measured once: they held the largest piles of
# unlabelled tiles in the corpus and none of them wants a label.
SKIP_SHEET = ("shadow", "darkness", "mask", "lighting", "_ore", "colors", "sky")
def _vanilla_sheet_names():
    """The names the base game has for a tilesheet, under both spellings it goes by.

    vanilla-maps.json lists ASSET names relative to Maps/, and twenty of them live in a folder:
    `Mines/mine`, `Mines/mine_dark`, and so on. A map does not name its tilesheet that way - it
    names it `mine` - so a straight compare said no, and seventeen sheet names that the vanilla
    mines draw were being called a mod's own art. That is the one answer this project is not
    allowed to get wrong, so both spellings count.
    """
    listed = json.load(open(os.path.join(HERE, "vanilla-maps.json"),
                            encoding="utf-8")).get("sheetAssets", [])
    names = {name.lower() for name in listed}
    names |= {name.rsplit("/", 1)[-1].lower() for name in listed}
    return names


VANILLA_SHEET_NAMES = _vanilla_sheet_names()


def content_hash(png_path):
    """The dump's own content hash for a sheet, taken from the file name it wrote.

    `<sheet>_<path hash>_<content hash>.png`. Reading it from the name rather than hashing the
    bytes again is not a shortcut for its own sake: the dump computed it over the PNG it wrote,
    and re-hashing here would compare our idea of the bytes with its idea of them.
    """
    if not png_path:
        return None
    stem = os.path.splitext(os.path.basename(png_path))[0]
    parts = stem.rsplit("_", 2)
    return parts[-1] if len(parts) == 3 and len(parts[-1]) == 8 else None


def load_index():
    path = os.path.join(HFDIR, "maps.json")
    if not os.path.exists(path):
        sys.exit(f"no dump at {path}")
    with io.open(path, encoding="utf-8") as handle:
        return json.load(handle)


def base_game_art(index):
    """sheet name -> the PNG holding the base game's OWN pixels for that name.

    THE MAPS THE BASELINE PASS VISITED say it outright: Label-BaseArt runs with no content mods
    at all, so whatever art a location recorded there is the base game's. That covers the 43
    tilesheets the vanilla maps actually draw in the season the dump was taken.

    THE OTHER THREE SEASONS no single dump can see from its maps. A dump is taken in one season
    and a location records the season in play, so a spring baseline knows nothing about the base
    game's fall_town. The index's artPng does: radiance_mapdump reads every seasonal sibling of a
    sheet directly, and the entry is first-writer-wins with Label-BaseArt always the first pass,
    so what stands there for a vanilla sheet name is vanilla.

    Guarded twice, because both guards caught something real. The name must be an asset the base
    game has, and the art must have come from a Maps/ asset rather than a mod's own file: four
    sheets in the 22 August dump were mod files under a vanilla name, one of them only because
    Windows does not distinguish Spring_Shadows from spring_Shadows.

    Lives here rather than in each caller because three tools now need it and every rule this
    project duplicated instead of shared has ended up disagreeing with itself.
    """
    art_of = {}
    for entry in index["locations"].values():
        if BASE_PASS not in (entry.get("from") or []):
            continue
        document = read_location(entry)
        for sheet, art in zip(document.get("sheets", []), document.get("sheetArt", [])):
            if content_hash(art):
                art_of.setdefault(sheet, art)
    source_of = index.get("artSrc") or {}
    for sheet, art in (index.get("artPng") or {}).items():
        if sheet in art_of or sheet.lower() not in VANILLA_SHEET_NAMES:
            continue
        if not (source_of.get(sheet) or "").replace("\\", "/").lower().startswith("maps/"):
            continue
        if content_hash(art):
            art_of[sheet] = art
    return art_of


def who_claims_each_map():
    """map name (lower) -> the mods that ship it or load it into Maps/<name>.

    A Solo pass holds one mod, so a version out of it is that mod's by construction. A BATCH
    pass holds several mods that only add maps of their own, and the pass alone cannot say which
    of them added any one map: crediting all of them gives every mod in the batch every other
    mod's work. A mod that SHIPS <name>.tmx or LOADS Maps/<name> is the one that made it, which
    is the same rule gen_locmods.py settled on after merely mentioning a name let recolours claim
    half the base game.

    Read from the mods on disk rather than from passes.json, because the profiles for a sweep in
    progress must not be regenerated to add a field: they are named by position and the mod list
    has changed since they were written.
    """
    claims = collections.defaultdict(set)
    try:
        import gensoloprofiles
    except Exception:
        return claims
    for key, mod in gensoloprofiles.scan_mods().items():
        owner = "%s/%s" % key
        for stem in mod.ships_maps:
            claims[stem.lower()].add(owner)
        for name in mod.loads:
            claims[name.lower()].add(owner)
    return claims


def profile_mods(name):
    """Every mod a pass actually enabled, as category/folder, from the profile it ran with."""
    path = os.path.join(PROFILE_DIR, name + ".json")
    try:
        with io.open(path, encoding="utf-8-sig") as handle:
            document = json.load(handle)
    except (OSError, ValueError):
        return set()
    return {"%s/%s" % (category, folder)
            for category, folders in (document.get("enabled") or {}).items()
            for folder in folders}


def load_pass_owners():
    """pass name -> what made it: a mod for a Solo pass, a list for a Batch."""
    path = os.path.join(HERE, "passes.json")
    if not os.path.exists(path):
        return {}
    with io.open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    owners = {}
    for entry in document.get("passes", []):
        named = [entry["mod"]] if entry.get("mod") else list(entry.get("mods", []))
        owners[entry["name"]] = {"kind": entry.get("kind", "batch"),
                                 "mod": entry.get("mod"),
                                 "mods": entry.get("mods", []),
                                 "touches": entry.get("touches", {}),
                                 # everything the pass had loaded that could have made a map.
                                 # Read from the profile on disk rather than from this file:
                                 # a Solo pass records its closure here and a Batch pass records
                                 # only its members, so a map belonging to a dependency the batch
                                 # dragged in - Custom_Atlantis is SVE's - matched nobody and
                                 # left every member of the batch credited with it.
                                 "candidates": (set(named) | set(entry.get("closure") or [])
                                                | profile_mods(entry["name"]))}
    return owners


def load_labelled_tiles():
    """sheet -> the set of tile indices that carry any label at all.

    The live folder at the HF-Studio root wins over labels/: the labeller writes there, and an
    older copy of a sheet in labels/ would report work as missing that is already done.
    """
    labelled = {}
    for folder in (HFDIR, os.path.join(HFDIR, "labels")):
        for path in glob.glob(os.path.join(folder, "*.labels.json")):
            sheet = os.path.basename(path)[:-len(".labels.json")]
            if sheet in labelled:
                continue
            try:
                with io.open(path, encoding="utf-8") as handle:
                    document = json.load(handle)
            except ValueError:
                continue
            marked = set()
            for tile, blob in (document.get("tiles") or {}).items():
                raw = base64.b64decode(blob)
                if len(raw) == TILE_PIXELS * TILE_PIXELS and any(raw):
                    marked.add(int(tile))
            labelled[sheet] = marked
    return labelled


def layout_hash(location_document):
    """What the map DRAWS, with no regard to which files it draws it from. Two mods whose only
    difference is a recolour land on one hash, which is what stops one Town becoming ninety."""
    layers = location_document.get("layers") or []
    digest = hashlib.sha1()
    for layer in layers:
        digest.update(str(layer.get("id", "")).encode())
        digest.update(b"\x00")
        digest.update(str(layer.get("cells", "")).encode())
        digest.update(b"\x01")
    return digest.hexdigest()[:12]


def build():
    index = load_index()
    owners = load_pass_owners()
    labelled = load_labelled_tiles()
    claims = who_claims_each_map()
    locations = index["locations"]
    # The tiles the GAME says are water. A raw unlabelled count is dominated by ground nobody
    # means to paint, so it ranks a mod by how big its maps are rather than by how much of this
    # project's work it holds; this is the number that answers "what should I open next".
    confirmed_water = {sheet: set(tiles) for sheet, tiles in (index.get("water") or {}).items()}

    # 1. the baseline: what each sheet name looks like with no mods at all
    baseline = {sheet: content_hash(art) for sheet, art in base_game_art(index).items()}

    # 2. every version of every place, grouped by what it draws
    version_of = {}
    for key, entry in locations.items():
        document = read_location(entry)
        version_of[key] = {
            "name": entry.get("name", key),
            "from": entry.get("from") or [],
            "used": entry.get("used") or [],
            "sheets": document.get("sheets", []),
            "art": [content_hash(a) for a in document.get("sheetArt", [])],
            "layout": layout_hash(document),
            "outdoors": bool(entry.get("outdoors")),
        }

    # WHAT THE BASE GAME ALREADY DRAWS.
    #
    # A location's identity in the dump is its cells AND its art, so a pass holding any pack
    # that repaints a shared tilesheet turns every vanilla map drawn with it into a new version.
    # One batch of six mods produced 242 of them - Backwoods, Beach, AdventureGuild - and every
    # mod in the pass was credited with all of them, none of which any of them had touched.
    #
    # Backwoods drawn cell for cell as the base game draws it is the base game's Backwoods; a
    # recolour did not make a new one. So a version whose LAYOUT the baseline also produced is
    # filed under the base game, and the fact that some mods draw it from different art is said
    # where it belongs, on the sheet and in the row's list of sheets the mods disagree about.
    # A mod loaded in EVERY pass explains nothing. The tooling rides along with all of them so
    # it survives every intersection, and where two passes made one version and share nothing
    # else, the fallback handed the map to the frameworks.
    #
    # Counted from what the passes actually enable AND from the tooling list, because the
    # intersection is only as strong as its weakest pass: the duplicate guard drops the
    # FarmTypeManager folder from the one pass whose own mod bundles a second copy, and that
    # single absence out of 843 profiles made a framework creditable again. It picked up 113
    # maps across 84 passes it was merely present in. A mod carried by construction is not a
    # candidate for having made anything, however many passes happen to leave it out.
    everywhere = {f"{category}/{folder}" for category, folder in gensoloprofiles.MUST}
    intersection = None
    for owner in owners.values():
        here = owner.get("candidates") or set()
        intersection = set(here) if intersection is None else (intersection & here)
    everywhere |= intersection or set()

    base_layouts = collections.defaultdict(set)
    for key, version in version_of.items():
        if BASE_PASS in version["from"]:
            base_layouts[version["name"]].add(version["layout"])

    groups = collections.defaultdict(list)
    for key, version in version_of.items():
        groups[(version["name"], version["layout"])].append(key)

    # 3. per mod
    per_mod = {}
    owner_of = {}
    for key, version in version_of.items():
        placed = collections.defaultdict(set)
        for cell in version["used"]:
            if cell < 0:
                continue
            sheet_index, tile = divmod(cell, CELL_SHEET_STRIDE)
            if sheet_index < len(version["sheets"]):
                placed[version["sheets"][sheet_index]].add(tile)
        # WHO MADE THIS VERSION, when several passes produced it.
        #
        # A pack built on Stardew Valley Expanded is swept with SVE loaded, so its pass dumps
        # SVE's maps as well as its own. Crediting every pass in `from` gave that pack all of
        # SVE's work: two mods reported the same 142,139 tiles left and only one had ever
        # touched a tile of it.
        #
        # A version that came out of several passes was made by something they all had loaded.
        # SVE is in the closure of every pass that produced SVE's Town; the pack is in only its
        # own. Where that narrows to nothing the passes' own mods are named, which is the honest
        # answer for a map two packs happen to build identically.
        if version["layout"] in base_layouts.get(version["name"], ()):
            continue
        shared = None
        for pass_name in version["from"]:
            here = (owners.get(pass_name) or {}).get("candidates") or set()
            shared = here if shared is None else (shared & here)
        shared = (shared or set()) - everywhere
        for pass_name in version["from"]:
            owner = owners.get(pass_name)
            named = ([owner["mod"]] if owner and owner.get("mod")
                     else (owner or {}).get("mods") or [pass_name])
            names = [n for n in named if n in shared] if shared else named
            if not names:
                names = sorted(shared) if shared else named
            # Still more than one, which means a batch: ask who actually ships or loads this map.
            #
            # Asked of everything the pass had loaded, not only of the mods the batch is named
            # for. Custom_Atlantis is Stardew Valley Expanded's, and SVE rides into that batch as
            # a dependency rather than as one of its six members, so intersecting with the member
            # list alone found nobody and left all six credited with it.
            #
            # The dump prefixes a mod-added location (Custom_, CapeMine_) and the mod's own file
            # is usually named without it, so the bare name is tried too.
            if len(names) > 1:
                place = version["name"].lower()
                bare = place.split("_", 1)[1] if place.startswith(("custom_", "capemine_")) else place
                pool = shared or set(names)
                claimed = (claims.get(place, set()) | claims.get(bare, set())) & pool
                if len(claimed) == 1:
                    names = sorted(claimed)
            for name in names:
                record = per_mod.setdefault(name, {
                    "mod": name, "kind": (owner or {}).get("kind", "unknown"),
                    "passes": set(), "maps": {}, "sheets": {}})
                record["passes"].add(pass_name)
                record["maps"][key] = version["name"]
                owner_of.setdefault(key, name)
                for sheet, tiles in placed.items():
                    if any(word in sheet.lower() for word in SKIP_SHEET):
                        continue
                    digest = version["art"][version["sheets"].index(sheet)]
                    base_digest = baseline.get(sheet)
                    # The baseline is asked FIRST, because it is evidence and the name list is
                    # only a list. A sheet the no-mods pass recorded is the base game's whatever
                    # vanilla-maps.json says, and the file turned out to be missing `cave`,
                    # `volcano_dungeon` and `volcano_caldera` outright.
                    if base_digest is not None:
                        mark = "vanilla" if base_digest == digest else "repainted"
                    elif sheet.lower() not in VANILLA_SHEET_NAMES:
                        mark = "own"
                    else:
                        mark = "unverified"
                    into = record["sheets"].setdefault(sheet, {"mark": mark, "placed": set()})
                    # A mod can draw one sheet name at two versions across its maps. Anything
                    # that is not settled vanilla is the answer that needs work, so it wins.
                    if mark != "vanilla":
                        into["mark"] = mark
                    into["placed"] |= tiles

    for record in per_mod.values():
        left = water_left = 0
        for sheet, info in record["sheets"].items():
            done = labelled.get(sheet, set())
            unlabelled = info["placed"] - done
            info["unlabelled"] = len(unlabelled)
            info["unlabelledWater"] = len(unlabelled & confirmed_water.get(sheet, set()))
            info["placedCount"] = len(info["placed"])
            left += info["unlabelled"]
            water_left += info["unlabelledWater"]
            del info["placed"]
        record["unlabelled"] = left
        record["unlabelledWater"] = water_left
        record["passes"] = sorted(record["passes"])
        record["usesOnlyVanillaArt"] = all(i["mark"] == "vanilla" for i in record["sheets"].values())
        record["done"] = left == 0

    base_layout_keys = {key for key, version in version_of.items()
                        if version["layout"] in base_layouts.get(version["name"], ())}
    return (index, owners, groups, version_of, per_mod, baseline, base_layout_keys, owner_of)


def read_location(entry):
    path = os.path.join(HFDIR, entry["file"])
    try:
        with io.open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return {}


def write_outputs(groups, version_of, per_mod, base_layout_keys, owner_of):
    # One row per real difference: a group is one place drawn one way. Where the mods in a group
    # disagree about art, the sheets they disagree about are named, because that is the whole of
    # the difference and hiding it would file two different pictures under one row.
    rows = []
    for (name, layout), keys in sorted(groups.items()):
        arts = {}
        for key in keys:
            version = version_of[key]
            arts[key] = dict(zip(version["sheets"], version["art"]))
        differing = sorted({sheet for sheet in set().union(*[set(a) for a in arts.values()])
                            if len({a.get(sheet) for a in arts.values()}) > 1})
        rows.append({"name": name, "layout": layout, "keys": sorted(keys),
                     "outdoors": version_of[keys[0]]["outdoors"],
                     "sheetsThatDiffer": differing})
    mods = sorted(per_mod.values(),
                  key=lambda r: (-r["unlabelledWater"], -r["unlabelled"], r["mod"]))
    # The answer to "whose is this version", per location key, so the labeller reads it instead
    # of keeping a second copy of the rule that can disagree with this one. A key that is absent
    # is the base game's.
    payload = {"places": rows, "mods": mods, "baseLayoutKeys": sorted(base_layout_keys),
               "owners": owner_of}
    with io.open(os.path.join(HFDIR, "modsheets.json"), "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False)
    if os.path.isdir(LABELLER):
        with io.open(os.path.join(LABELLER, "modsheets.js"), "w", encoding="utf-8") as handle:
            handle.write("// Which mod uses which sheet, whose art it is, and what is left.\n"
                         "// Written by tools/labelops/modsheets.py after a per-mod sweep.\n"
                         "window.MODSHEETS=")
            json.dump(payload, handle, ensure_ascii=False)
            handle.write(";\n")
    return rows, mods


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", action="store_true", help="print only, write nothing")
    arguments = parser.parse_args()

    index, owners, groups, version_of, per_mod, baseline, base_layout_keys, owner_of = build()
    rows, mods = (write_outputs(groups, version_of, per_mod, base_layout_keys, owner_of) if not arguments.report
                  else ([], sorted(per_mod.values(), key=lambda r: -r["unlabelled"])))

    marks = collections.Counter(info["mark"] for record in per_mod.values()
                                for info in record["sheets"].values())
    finished = sum(1 for record in per_mod.values() if record["done"])
    vanilla_only = sum(1 for record in per_mod.values() if record["usesOnlyVanillaArt"])
    print(f"{len(version_of):,} map versions -> {len(groups):,} distinct layouts")
    print(f"{len(per_mod):,} mods in the dump")
    print(f"  {finished:,} have nothing left to label, {vanilla_only:,} draw only vanilla art")
    print(f"  sheet uses by mark: {dict(marks)}")
    print(f"  baseline sheets: {len(baseline):,}")
    water = sum(r["unlabelledWater"] for r in per_mod.values())
    print(f"  unlabelled tiles the game itself calls water: {water:,}")
    print("")
    print("most work left, ranked by the water the game confirmed:")
    for record in mods[:15]:
        if not record["unlabelled"]:
            break
        print(f"  {record['unlabelledWater']:>6} water {record['unlabelled']:>8} all   "
              f"{record['mod']}  ({len(record['sheets'])} sheets, {len(record['maps'])} maps)")
    if not arguments.report:
        print(f"\nwrote {os.path.join(HFDIR, 'modsheets.json')}")


if __name__ == "__main__":
    main()
