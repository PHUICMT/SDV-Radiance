"""Find the passes SMAPI quietly refused a mod in, and write corrected profiles to run again.

    python tools/labelops/redoprofiles.py           say what needs redoing and why
    python tools/labelops/redoprofiles.py --write   write the Redo- profiles and extend passes.json

A pass whose mod SMAPI would not load still starts the game, still dumps, still reports success,
and contains none of the maps that mod exists to provide. It reads exactly like a mod that
changes nothing. Over the first sixty passes of the 23 August sweep, fifteen were that, and
nearly all of them contributed no map version at all.

Two causes, and only one of them is ours:

  * a dependency that IS on disk was never indexed, so the profile shipped without it. Both
    reasons for that are now fixed in gensoloprofiles.py - a manifest with a trailing `//`
    comment did not parse, and mods in the categories that get no pass of their own were not
    scanned at all, so nothing in them could ever be resolved as a dependency. Between them
    they cost 97 resolvable ids.
  * a dependency that is nowhere on disk, which no profile can satisfy. Cherry's farmhouse asks
    for mabelsyrup.farmhouse and nothing here declares that id. Those are reported once, with
    the ids they want, rather than retried forever.

Redo passes are named apart from the originals on purpose. The dump records the pass a version
came from, Solo-015 is already in it, and rewriting Solo-015 to mean something else would make
the record of a finished pass disagree with the profile of that name.
"""
import argparse, collections, json, os, sys
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gensoloprofiles as solo
import sweepwatch

GAME = solo.GAME
PROFILE_DIR = solo.PROFILE_DIR
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")


def passes_on_record():
    with open(os.path.join(HERE, "passes.json"), encoding="utf-8") as handle:
        return json.load(handle)


def refusals():
    """pass name -> the mods SMAPI would not load in it, for every pass with a log."""
    out = {}
    log_dir = os.path.join(HFDIR, "sweep-logs")
    if not os.path.isdir(log_dir):
        return out
    for filename in sorted(os.listdir(log_dir)):
        if not filename.endswith(".log"):
            continue
        name = filename[:-4]
        if not name.startswith(("Solo-", "Batch-", "Redo-")):
            continue
        new_versions, refused, crashed, patch_failed, missing_sheets = sweepwatch.inspect(name)
        if refused or patch_failed:
            out[name] = {"refused": refused, "new": new_versions, "crashed": crashed,
                         "patchFailed": patch_failed, "missingSheets": missing_sheets}
    return out


def sheet_providers():
    """png file name (lower) -> the mod folders that ship it.

    A Content Patcher patch fails when the map it loads names a tilesheet nothing in the profile
    provides, and the mod is usually not at fault twice: its manifest asks for what it needs to
    RUN, and a tilesheet its map file references is not that. Both patch failures in the first
    284 passes wanted spring_daisyextras.png, which eleven mods in this library use, so the fix
    is worth having: find who ships the PNG and put them in the redo.
    """
    providers = defaultdict(set)
    for root in solo.ROOTS:
        base = os.path.join(GAME, root)
        if not os.path.isdir(base):
            continue
        for category in sorted(os.listdir(base)):
            category_path = os.path.join(base, category)
            if not os.path.isdir(category_path):
                continue
            for folder in sorted(os.listdir(category_path)):
                mod_path = os.path.join(category_path, folder)
                if not os.path.isdir(mod_path):
                    continue
                for directory, subdirectories, files in os.walk(mod_path):
                    subdirectories[:] = [d for d in subdirectories
                                         if d not in (".git", "node_modules", "__pycache__")]
                    for filename in files:
                        if filename.lower().endswith(".png"):
                            providers[filename.lower()].add((category, folder))
    return providers


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    record = passes_on_record()
    by_name = {entry["name"]: entry for entry in record["passes"]}
    hurt = refusals()
    if not hurt:
        print("no pass has a real SMAPI refusal in its log")
        return

    mods = solo.scan_mods()
    ships_sheet = sheet_providers()
    by_id = {}
    # How many mods declare a dependency on each mod, used to tell a framework everybody builds
    # on from a pack that happens to ship a file with the same name.
    depended_on = collections.Counter()
    for mod in mods.values():
        for unique_id in mod.unique_ids:
            by_id.setdefault(unique_id.lower(), mod.key)

    for mod in mods.values():
        for dependency in set(mod.dependencies):
            owner = by_id.get(dependency.lower())
            if owner:
                depended_on[owner] += 1

    def closure(key):
        seen, stack = {key}, [key]
        missing = set()
        while stack:
            at = stack.pop()
            for dependency in mods[at].dependencies:
                found = by_id.get(dependency.lower())
                if not found:
                    missing.add(dependency)
                elif found not in seen:
                    seen.add(found)
                    stack.append(found)
        return seen, missing

    must = {key for key in solo.MUST if key in mods}
    plan, unfixable, unchanged = [], [], []

    for name, detail in sorted(hurt.items()):
        entry = by_name.get(name) or {}
        owners = [entry["mod"]] if entry.get("mod") else entry.get("mods", [])
        for owner in owners:
            category, _, folder = owner.partition("/")
            key = (category, folder)
            if key not in mods:
                continue
            selection, missing = closure(key)
            # Whatever a failed patch could not find, add whoever ships it.
            #
            # Several mods can ship a PNG by one name: spring_daisyextras.png comes from
            # DaisyNiko's Tilesheets and from a grass recolour, and enabling the recolour would
            # satisfy the load while quietly changing what the sheet looks like - which is worse
            # than the patch failing, because the dump would then be labelled against art the
            # mod's players never see. So where there is a choice, take the pack other mods
            # actually declare a dependency on. That is the canonical provider by the only
            # measure available, and it is a count rather than a guess about names.
            for sheet in detail.get("missingSheets") or []:
                providers = sorted(ships_sheet.get(sheet.lower(), ()))
                if len(providers) > 1:
                    providers.sort(key=lambda p: (-depended_on.get(p, 0), p))
                    providers = providers[:1]
                selection.update(providers)
            # The recorded closure leaves out the tooling every profile carries anyway, so the
            # comparison has to add it back. Without that, every redo looked like it gained
            # ContentPatcher and the list of real gains was buried.
            was = set(entry.get("closure") or []) | {f"{c}/{f}" for c, f in must}
            now = {f"{c}/{f}" for c, f in selection - {key}}
            gained = now - was
            # A duplicate is the other way a pass can be worth redoing, and it does not look
            # like a gain: nothing is missing, the same mod id simply arrives from two folders
            # and SMAPI refuses BOTH copies, so the pass runs with that content absent. The
            # profile that fixes it is SMALLER than the one that failed, which is why the
            # gained-a-dependency test alone called these unfixable.
            deduped = solo.without_duplicate_ids(selection | must, mods, keep={key})
            shed = sorted(f"{c}/{f}" for c, f in (selection | must) - deduped)
            if missing:
                unfixable.append((name, owner, sorted(missing)))
            if not gained and not shed:
                unchanged.append((name, owner))
                continue
            plan.append({"was": name, "mod": owner, "key": key, "selection": selection,
                         "gained": sorted(gained), "shed": shed, "missing": sorted(missing),
                         "touches": entry.get("touches", {})})

    print(f"{len(hurt)} pass(es) had a mod SMAPI refused")
    print(f"  {len(plan)} can be redone with a fuller closure")
    print(f"  {len(unchanged)} gain nothing from a redo (the refusal was not a missing dependency)")
    shedding = [item for item in plan if item["shed"]]
    if shedding:
        print(f"  {len(shedding)} of those redos also drop a second copy of a mod SMAPI would "
              f"have refused")
    print(f"  {len(unfixable)} ask for a mod that is nowhere on disk")
    for name, owner, missing in unfixable[:8]:
        print(f"     {name} {owner}: {', '.join(missing[:3])}")
    for item in plan[:10]:
        print(f"   {item['was']} -> {item['mod']}  gains {', '.join(item['gained'][:3])}"
              f"{' ...' if len(item['gained']) > 3 else ''}")

    # The unsatisfiable ones are a shopping list, not a bug: those mods were never
    # downloaded, so every pass depending on them keeps coming back empty and reporting
    # success. Written out with who wants each id, so the decision to fetch them can be
    # made from the list rather than from a line that scrolled past.
    wanted = defaultdict(set)
    for name, owner, missing in unfixable:
        for unique_id in missing:
            wanted[unique_id].add(owner)

    # A mod that cannot load takes down everything that depends on it. TMXL Map Toolkit is here
    # and cannot load, because PyTK is not, and twenty-odd map packs need TMXL - so counting only
    # who names an id directly puts the download that unblocks the most mods near the bottom.
    needs = defaultdict(set)                       # mod key -> the mod keys it depends on
    for key, mod in mods.items():
        for dependency in mod.dependencies:
            found = by_id.get(dependency.lower())
            if found:
                needs[key].add(found)
    reach = {}
    for unique_id, direct in wanted.items():
        blocked = {tuple(o.split("/", 1)) for o in direct}
        blocked = {k for k in blocked if k in mods}
        changed = True
        while changed:
            changed = False
            for key, wants in needs.items():
                if key not in blocked and wants & blocked:
                    blocked.add(key)
                    changed = True
        reach[unique_id] = blocked
    if wanted:
        out = os.path.join(HFDIR, "missing-dependencies.json")
        with open(out, "w", encoding="utf-8") as handle:
            json.dump({"//": "SMAPI refused to load these mods because these ids are on no "
                             "manifest here. Until they are downloaded, every pass for the "
                             "mods listed against them dumps none of their maps and still "
                             "reports success.",
                       "wanted": {k: {"askedForBy": sorted(v),
                                      "modsBlocked": len(reach.get(k, ())),
                                      "throughThem": sorted("%s/%s" % m for m in reach.get(k, ()))[:40]}
                                  for k, v in sorted(wanted.items())}},
                      handle, ensure_ascii=False, indent=1)
        owners = {owner for names in wanted.values() for owner in names}
        print(f"{len(wanted)} dependency id(s) are on no manifest here, wanted by "
              f"{len(owners)} mod(s): {out}")
        # Ranked, because they are not worth the same. One of these ids blocks a dozen passes
        # and the rest block one each, so the first line of this list is most of the payoff.
        print("  worth fetching first, counting what each unblocks in turn:")
        for unique_id in sorted(wanted, key=lambda k: (-len(reach.get(k, ())), k))[:6]:
            print(f"    {len(reach.get(unique_id, ())):>3} mod(s) blocked by {unique_id}"
                  f"  (asked for directly by {len(wanted[unique_id])})")

    if not arguments.write or not plan:
        if plan and not arguments.write:
            print("\npass --write to create the Redo- profiles")
        return

    start = 1 + max([int(p["name"].split("-")[1]) for p in record["passes"]
                     if p["name"].startswith("Redo-")] or [0])
    added = []
    for offset, item in enumerate(plan):
        name = f"Redo-{start + offset:03d}"
        enabled = defaultdict(list)
        for category, folder in sorted(solo.without_duplicate_ids(
                item["selection"] | must, mods, keep=item["key"])):
            enabled[category].append(folder)
        document = {
            "name": name, "kind": "solo", "created": "2026-08-23",
            "note": (f"{item['mod']} again, with the dependencies its first pass ({item['was']}) "
                     f"was missing. SMAPI refused to load it there, so that pass reported success "
                     f"and dumped none of this mod's maps."),
            "mod": item["mod"], "touches": item["touches"],
            "redoOf": item["was"], "gained": item["gained"], "shed": item["shed"],
            "enabled": dict(enabled)}
        with open(os.path.join(PROFILE_DIR, f"{name}.json"), "w", encoding="utf-8") as handle:
            json.dump(document, handle, ensure_ascii=False, indent=2)
        added.append({"name": name, "kind": "solo", "mod": item["mod"],
                      "touches": item["touches"], "redoOf": item["was"],
                      "modCount": sum(len(v) for v in enabled.values())})
    record["passes"].extend(added)
    with open(os.path.join(HERE, "passes.json"), "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False, indent=1)
    print(f"\nwrote {len(added)} Redo- profile(s) and extended passes.json")
    print("run them with:  python tools/labelops/run.py --only sweep")


if __name__ == "__main__":
    main()
