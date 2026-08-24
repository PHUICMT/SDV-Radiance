"""One pass per mod that changes a VANILLA map, so the dump holds Town exactly as that mod's
player sees it, and batched passes for the mods that only add maps of their own.

    python tools/labelops/gensoloprofiles.py            write the profiles and passes.json
    python tools/labelops/gensoloprofiles.py --dry      count and list, write nothing

Why this replaces the graph colouring in genmapprofiles.py for the labeller:

  That colouring let EditMap patches pile up in one pass on purpose, because a label belongs to
  a tilesheet tile and a composed Town still draws real tiles. True, and it produced 96 versions
  of Town of which none was the Town any single mod's player sees, filed under names like
  MapPass-65 because nobody could say which of the seven packs in the pass had made it.
  A mod that edits a vanilla map gets a pass of its own here. Its dependencies ride along
  (a pack built on SVE is seen on SVE's Town, which is the truth), and every other mod stays
  out, so a version of Town is attributable to one mod by construction rather than by guess.

  Mods that only ADD maps cannot change Town, so they are batched as before, coloured so that no
  two closures in a batch claim one Maps/ target through DIFFERENT mods. Two packs that both
  depend on SVE share a batch: SVE's Town is the same Town either way and merges by stamp.

What counts as changing a vanilla map, judged on the mod ITSELF (not its dependencies):
  * a Content Patcher EditMap or Load whose target is Maps/<name> with <name> in
    vanilla-maps.json (every base-game location the dump has seen plus every map asset in
    Content/Maps that is not a tilesheet)
  * a shipped <name>.tmx / .tbin with such a name
  * a Maps/ target with a token this script cannot resolve (Maps/{{Farm}} and the like): taken
    as vanilla, so the mod gets its own pass rather than being silently wrong

Pass names carry no meaning on purpose (Solo-001, Batch-01): the meaning is in the profile
document (`mod`, `kind`, `touches`, `closure`) and in passes.json, which whoowns.py and the
labeller read. Label-BaseArt is kept as the vanilla baseline and always runs first.
"""
import argparse, json, os, re, sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from whopaintedtile import tolerant_json as tolerant_json_text   # the character-walking one

sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HERE = os.path.dirname(os.path.abspath(__file__))
PROFILE_DIR = os.path.join(GAME, "mod-profiles")
ROOTS = ["Mods", "Mods (disabled)"]
SKIP_CATS = {"01_Characters-Anime", "06_Localization", "07_NSFW", "08_Misc-Fun", "05_Gameplay"}
BANNED = {"DynamicShader", "DynamicReflections", "SmoothCamera", "HeightFramework"}
MUST = [("03_Graphics-FX", "SDV-Radiance"), ("00_Frameworks", "SDV-AgentBridge"),
        ("00_Frameworks", "ContentPatcher"), ("00_Frameworks", "ConsoleCommands"),
        ("00_Frameworks", "GenericModConfigMenu"), ("00_Frameworks", "AlternativeTextures"),
        ("00_Frameworks", "SpaceCore"), ("00_Frameworks", "FarmTypeManager"),
        ("00_Frameworks", "SolidFoundations"), ("00_Frameworks", "SaveBackup"),
        ("00_Frameworks", "CustomCompanions"), ("00_Frameworks", "MailFrameworkMod"),
        ("00_Frameworks", "EscasModdingPlugins"), ("00_Frameworks", "SecretNoteFramework"),
        ("00_Frameworks", "GoldenWalnutFramework"), ("00_Frameworks", "Unlockable Bundles"),
        ("00_Frameworks", "MappingExtensionsAndExtraProperties"),
        ("00_Frameworks", "CrossModCompatibilityTokens")]
SEASONS = ("spring", "summer", "fall", "winter")
SKIP_DIRS = {".git", "node_modules", "__pycache__"}
MAXIMUM_PATCH_BYTES = 8 * 1024 * 1024
MAP_EXTENSIONS = (".tmx", ".tbin")

vanilla_maps = {name.lower() for name in
                json.load(open(os.path.join(HERE, "vanilla-maps.json"), encoding="utf-8"))["all"]}


def map_targets(target, mod_id=""):
    """(name, resolved) for every Maps/ target one Target string names. Unresolved means a
    token other than {{season}} or {{ModId}} sat in the name; the caller decides what to do
    with that. {{ModId}} is the mod's own unique id, so a map named through it is the mod's own
    map and is resolved to that name rather than counted as an unreadable vanilla claim."""
    out = []
    for one in str(target or "").split(","):
        one = one.strip().replace("\\", "/")
        if not one.lower().startswith("maps/"):
            continue
        name = one[5:]
        if mod_id:
            name = re.sub(r"\{\{\s*ModId\s*\}\}", mod_id, name, flags=re.I)
        if "{{" in name:
            head, _, rest = name.partition("{{")
            token, _, tail = rest.partition("}}")
            if token.strip().lower() == "season" and "{{" not in tail:
                out.extend((head + season + tail, True) for season in SEASONS)
            else:
                out.append((name, False))
            continue
        out.append((name, True))
    return out


def parse_jsonc(raw):
    """A Content Patcher file or a SMAPI manifest, however it was written, or None.

    Strict JSON first, then the character-walking parser in whopaintedtile.py. That parser is
    used rather than a regex here because the regex this replaces was wrong in a way that reads
    as a mod claiming nothing: it stripped `//` only at the start of a line, so a manifest whose
    MinimumApiVersion carried a trailing comment failed to parse, its UniqueID was never indexed,
    and every mod depending on it was given a pass with that dependency missing. SMAPI then
    refused to load the mod and the pass reported success while dumping none of the maps it
    existed for. Anchoring the regex the other way is worse: it eats every https:// in the file.
    """
    try:
        return json.loads(raw)
    except Exception:
        pass
    try:
        return tolerant_json_text(raw)
    except Exception:
        return None


def read_json(path):
    try:
        raw = open(path, encoding="utf-8-sig", errors="replace").read()
    except OSError:
        return None
    return parse_jsonc(raw)


OUTDOOR_MAPS = {name.lower() for name in
                json.load(open(os.path.join(HERE, "vanilla-maps.json"), encoding="utf-8")).get("outdoors", [])}
OUTDOOR_NAME = re.compile(r"^(Town|Forest|Beach|Mountain|Woods|Railroad|Backwoods|BusStop|Desert|"
                          r"Farm|Island|Summit|Caldera|Mine$|Mines/|Tunnel|WitchSwamp|BugLand|Sewer)", re.I)


def touches_outdoors(mod):
    """Whether any vanilla map this mod changes is an outdoor one: where the water is, so these
    passes run first and a run cut short has still dumped the maps that matter most."""
    return any(name.lower() in OUTDOOR_MAPS or OUTDOOR_NAME.match(name)
               for name in mod.touches_vanilla)


def version_order(text):
    """A manifest Version as numbers that sort. `1.24.1-alpha.20240226` ranks below `1.24.1`,
    which is what SMAPI means when it asks for something newer than the alpha."""
    head = str(text or "").split("-")[0]
    parts = [int(piece) for piece in re.findall(r"\d+", head)[:4]]
    parts += [0] * (4 - len(parts))
    return tuple(parts) + ((0,) if "-" in str(text or "") else (1,))


def without_duplicate_ids(selection, mods, keep=()):
    """Drop folders that would put the same mod id in one profile twice.

    SMAPI refuses a mod outright when it finds two copies installed, and it refuses BOTH, so a
    pass that enables two folders carrying the same id loses that content entirely and still
    reports success. The library has this honestly rather than by accident: Garden Village ships
    inside three separate map mods, FarmTypeManager exists as its own framework folder and again
    bundled inside Standard Farm Expanded, Downtown Zuzu ships in the English mod and in the
    Chinese one. Twenty-two refusals across the 23 August sweep were this and nothing else.

    The mods the pass exists for are kept whatever else has to go. Then a folder that carries
    something NOBODY else in this selection carries, because dropping it would lose content no
    other folder can supply: that is what keeps Standard Farm Expanded, whose only clash is the
    FarmTypeManager copy it bundles, over the framework's own folder - the bundle provides the
    framework anyway, the reverse is not true.

    Then the NEWER copy. Eighty-one ids in this library live in two folders and many of those
    pairs are a mod and its unofficial 1.6 update sitting side by side; picking the older one
    gets it refused as no longer compatible, which is a different refusal and just as fatal.

    This cannot fix a mod that ships several variants of ITSELF - Dirt To Grass has seven folders
    for seven recolours and one id - because there is no second folder to prefer. Those refuse
    however the profile is built.
    """
    keep = set(keep)
    holders = defaultdict(set)
    for key in selection:
        for unique in mods[key].unique_ids:
            holders[unique.lower()].add(key)

    def rank(key):
        mod = mods[key]
        only_here = sum(1 for unique in mod.unique_ids if len(holders[unique.lower()]) == 1)
        newest = max([version_order(v) for v in mod.versions] or [()])
        return (key not in keep, -only_here, [-part for part in newest], -len(mod.unique_ids), key)

    order = sorted(selection, key=rank)
    claimed, kept = set(), set()
    for key in order:
        ids = {unique.lower() for unique in mods[key].unique_ids}
        if ids & claimed:
            continue
        claimed |= ids
        kept.add(key)
    return kept


class Mod:
    def __init__(self, category, folder):
        self.key = (category, folder)
        self.unique_ids = []
        self.versions = []               # the Version of each manifest, in the same order
        self.dependencies = []
        self.ships_maps = set()          # map file stems it ships
        self.loads = set()               # Maps/ targets it Loads (resolved names)
        self.edits = set()               # Maps/ targets it EditMaps (resolved names)
        self.repaints = set()            # Maps/ targets it EditImages (sheet recolours)
        self.unresolved = set()          # Maps/ targets with a token nobody can expand here

    @property
    def touches_vanilla(self):
        """The vanilla maps this mod itself changes, and how. Empty means it only adds."""
        touches = {}
        for name in self.edits:
            if name.lower() in vanilla_maps:
                touches.setdefault(name, set()).add("editmap")
        for name in self.loads:
            if name.lower() in vanilla_maps:
                touches.setdefault(name, set()).add("load")
        for stem in self.ships_maps:
            if stem.lower() in vanilla_maps:
                touches.setdefault(stem, set()).add("tmx")
        for name in self.unresolved:
            touches.setdefault(name, set()).add("unresolved-token")
        return touches

    @property
    def is_player(self):
        return bool(self.ships_maps or self.loads or self.edits or self.repaints or self.unresolved)


def scan_mods():
    mods = {}
    for root in ROOTS:
        base = os.path.join(GAME, root)
        if not os.path.isdir(base):
            continue
        for category in sorted(os.listdir(base)):
            category_path = os.path.join(base, category)
            # Every category is SCANNED, including the ones no mod here will be given a pass of
            # its own from. A map mod's dependency can live in any of them, and a dependency that
            # was never indexed cannot be resolved, so the mod ships without it and SMAPI refuses
            # to load the mod at all - a pass that reports success and dumps none of the maps it
            # exists for. Which categories may HOLD a pass is decided in build().
            if not os.path.isdir(category_path):
                continue
            for folder in sorted(os.listdir(category_path)):
                mod_path = os.path.join(category_path, folder)
                if not os.path.isdir(mod_path) or folder in BANNED:
                    continue
                mod = Mod(category, folder)
                walked = []
                for directory, subdirectories, files in os.walk(mod_path):
                    subdirectories[:] = [d for d in subdirectories if d not in SKIP_DIRS]
                    walked.append((directory, files))
                    for filename in files:
                        if filename.lower() == "manifest.json":
                            manifest = read_json(os.path.join(directory, filename))
                            if manifest and manifest.get("UniqueID"):
                                mod.unique_ids.append(manifest["UniqueID"])
                                mod.versions.append(str(manifest.get("Version") or ""))
                for directory, files in walked:
                    for filename in files:
                        lower = filename.lower()
                        if lower.endswith(MAP_EXTENSIONS):
                            mod.ships_maps.add(os.path.splitext(filename)[0])
                        elif lower == "manifest.json":
                            manifest = read_json(os.path.join(directory, filename))
                            if not manifest:
                                continue
                            for dependency in (manifest.get("Dependencies") or []):
                                if dependency.get("IsRequired") is not False and dependency.get("UniqueID"):
                                    mod.dependencies.append(dependency["UniqueID"])
                            content_pack_for = manifest.get("ContentPackFor", {})
                            if isinstance(content_pack_for, dict) and content_pack_for.get("UniqueID"):
                                mod.dependencies.append(content_pack_for["UniqueID"])
                        elif lower.endswith(".json") and lower != "config.json":
                            path = os.path.join(directory, filename)
                            try:
                                if os.path.getsize(path) > MAXIMUM_PATCH_BYTES:
                                    continue
                                raw = open(path, encoding="utf-8-sig", errors="replace").read()
                            except OSError:
                                continue
                            if '"Target"' not in raw or '"Changes"' not in raw:
                                continue
                            document = parse_jsonc(raw)
                            if not isinstance(document, dict):
                                continue
                            for patch in (document.get("Changes") or []):
                                if not isinstance(patch, dict):
                                    continue
                                action = str(patch.get("Action") or "").lower()
                                if action not in ("load", "editmap", "editimage"):
                                    continue
                                for name, resolved in map_targets(patch.get("Target"),
                                                                  mod.unique_ids[0] if mod.unique_ids else ""):
                                    if not resolved:
                                        if action != "editimage":
                                            mod.unresolved.add(name)
                                        continue
                                    {"load": mod.loads, "editmap": mod.edits,
                                     "editimage": mod.repaints}[action].add(name)
                if mod.unique_ids or mod.ships_maps:
                    own = {u.lower() for u in mod.unique_ids}
                    mod.dependencies = sorted({d for d in mod.dependencies if d.lower() not in own})
                    mods[mod.key] = mod
    return mods


def build(dry):
    mods = scan_mods()
    by_id = {}
    for mod in mods.values():
        for unique_id in mod.unique_ids:
            by_id.setdefault(unique_id.lower(), mod.key)

    def closure(key):
        seen, stack = {key}, [key]
        while stack:
            at = stack.pop()
            for dependency in mods[at].dependencies:
                next_key = by_id.get(dependency.lower())
                if next_key and next_key not in seen:
                    seen.add(next_key)
                    stack.append(next_key)
        return seen

    players = sorted(key for key, mod in mods.items()
                     if mod.is_player and key[0] not in SKIP_CATS)
    editors = [key for key in players if mods[key].touches_vanilla]
    editors.sort(key=lambda key: (not touches_outdoors(mods[key]), key))
    adders = [key for key in players if not mods[key].touches_vanilla]

    # Batch colouring for the adders. Two closures clash on a Maps/ target only when DIFFERENT
    # mods claim it: the same SVE in two closures is one SVE and one Town.
    claimants = {}
    for key in adders:
        mine = defaultdict(set)
        for member in closure(key):
            mod = mods[member]
            for name in mod.loads:
                mine[("load", name.lower())].add(member)
            for name in mod.repaints:
                mine[("editimage", name.lower())].add(member)
            for name in mod.edits:
                mine[("editmap", name.lower())].add(member)
        claimants[key] = mine
    by_target = defaultdict(set)
    for key, mine in claimants.items():
        for target in mine:
            by_target[target].add(key)
    adjacency = defaultdict(set)
    for target, keys in by_target.items():
        keys = sorted(keys)
        for i in range(len(keys)):
            for j in range(i + 1, len(keys)):
                a, b = keys[i], keys[j]
                if claimants[a][target] != claimants[b][target]:
                    adjacency[a].add(b)
                    adjacency[b].add(a)
    order = sorted(adders, key=lambda key: -len(adjacency[key]))
    colour = {}
    for key in order:
        taken = {colour[n] for n in adjacency[key] if n in colour}
        c = 0
        while c in taken:
            c += 1
        colour[key] = c
    batch_count = max(colour.values()) + 1 if colour else 0
    load = defaultdict(int)
    colour = {}
    for key in order:
        taken = {colour[n] for n in adjacency[key] if n in colour}
        c = min((x for x in range(batch_count) if x not in taken), key=lambda x: (load[x], x))
        colour[key] = c
        load[c] += 1

    def resolve(selection):
        have = {u.lower() for key in selection for u in mods[key].unique_ids}
        changed = True
        while changed:
            changed = False
            for key in list(selection):
                for dependency in mods[key].dependencies:
                    lower = dependency.lower()
                    if lower in have or lower not in by_id:
                        continue
                    next_key = by_id[lower]
                    if next_key not in selection:
                        selection.add(next_key)
                        have.update(u.lower() for u in mods[next_key].unique_ids)
                        changed = True
        return selection

    must = {key for key in MUST if key in mods}
    passes = []

    def profile_document(name, kind, selection, note, extra):
        enabled = defaultdict(list)
        for category, folder in sorted(selection):
            enabled[category].append(folder)
        document = {"name": name, "kind": kind, "created": "2026-08-22", "note": note,
                    **extra, "enabled": dict(enabled)}
        if not dry:
            with open(os.path.join(PROFILE_DIR, f"{name}.json"), "w", encoding="utf-8") as handle:
                json.dump(document, handle, ensure_ascii=False, indent=2)
        passes.append({"name": name, "kind": kind, **extra,
                       "modCount": sum(len(v) for v in enabled.values())})

    for number, key in enumerate(editors, 1):
        mod = mods[key]
        selection = without_duplicate_ids(resolve(set(must) | {key}), mods, keep={key})
        touches = {name: sorted(ways) for name, ways in sorted(mod.touches_vanilla.items())}
        profile_document(
            f"Solo-{number:03d}", "solo", selection,
            f"{key[0]}/{key[1]} alone with its dependencies and the tooling, so every vanilla "
            f"map it changes is dumped as this mod's player sees it.",
            {"mod": f"{key[0]}/{key[1]}", "touches": touches,
             "closure": sorted(f"{c}/{f}" for c, f in selection - must - {key})})

    for c in range(batch_count):
        members = sorted(key for key, value in colour.items() if value == c)
        selection = without_duplicate_ids(resolve(set(must) | set(members)), mods,
                                          keep=set(members))
        profile_document(
            f"Batch-{c + 1:02d}", "batch", selection,
            "Mods that only add maps of their own, batched so that no two closures claim one "
            "Maps/ target through different mods. Any vanilla map in this pass belongs to a "
            "dependency that has a Solo pass of its own and merges with it by stamp.",
            {"mods": [f"{k[0]}/{k[1]}" for k in members]})

    if not dry:
        with open(os.path.join(HERE, "passes.json"), "w", encoding="utf-8") as handle:
            json.dump({"//": "Every pass of the per-mod sweep, in run order after Label-BaseArt. "
                             "Solo = one vanilla-map editor alone; Batch = map adders that do not "
                             "clash. Built by gensoloprofiles.py.",
                       "passes": passes}, handle, ensure_ascii=False, indent=1)
        with open(os.path.join(HERE, "mappasses.json"), "w", encoding="utf-8") as handle:
            json.dump([p["name"] for p in passes], handle, indent=1)

    unresolved_only = sum(1 for key in editors
                          if all(ways == {"unresolved-token"} for ways in mods[key].touches_vanilla.values()))
    print(f"{len(mods)} mods scanned, {len(players)} touch maps")
    outdoor_first = sum(1 for key in editors if touches_outdoors(mods[key]))
    print(f"  {len(editors)} change a vanilla map -> {len(editors)} Solo passes "
          f"({outdoor_first} touch an outdoor map and run first; "
          f"{unresolved_only} only through a token this script cannot read)")
    print(f"  {len(adders)} only add maps -> {batch_count} Batch passes "
          f"(largest {max(load.values()) if load else 0} mods)")
    print(f"  {len(passes) + 1} passes with Label-BaseArt; {'nothing written (dry)' if dry else 'profiles written'}")
    if dry:
        for p in passes[:8]:
            print("   ", p["name"], p.get("mod") or f"{len(p.get('mods', []))} mods", p.get("touches", ""))
    return passes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry", action="store_true", help="count and list, write nothing")
    arguments = parser.parse_args()
    build(arguments.dry)


if __name__ == "__main__":
    main()
