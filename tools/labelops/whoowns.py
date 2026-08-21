"""Name the MOD behind each version of a place, not just the profile that dumped it.

The dump records which profiles produce each version of a location, and a profile is a hundred
mods with a name like MapPass-07 - true, and useless for deciding what to label. This narrows
it: of the mods enabled in those profiles, which ones actually claim that location, either by
shipping its map file or by patching Maps/<name>.

    python tools/labelops/whoowns.py                     write locowner.js for the labeller
    python tools/labelops/whoowns.py --show Town         who claims one place, and in which pass

Honest about its limits, and they matter:
  * where several enabled packs claim one location only ONE of them won, and which is load
    order. All of them are reported and the labeller says so rather than picking.
  * a patch switched off in a pack's config still counts as a claim.
  * a mod that swaps a map from C# rather than from a patch list is invisible here.
  * a place NO enabled pack claims is the base game's, which is the answer for most of them.
"""
import argparse, collections, json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from whopaintedtile import tolerant_json          # the character-walking one, hard-won

sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PROFILE_DIR = os.path.join(GAME, "mod-profiles")
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
LABELER = r"E:\Games\GamesMods\DevStardew\SDV-HeightFramework\tools\labeler"
ROOTS = [os.path.join(GAME, "Mods"), os.path.join(GAME, "Mods (disabled)")]
SEASONS = ("spring", "summer", "fall", "winter")
MAP_EXT = (".tmx", ".tbin", ".tmj")
SKIP_DIRS = {".git", "node_modules", "__pycache__"}
MAXIMUM_PATCH_BYTES = 8 * 1024 * 1024


def targets_of(patch):
    """Every Maps/ location name one patch claims, seasons expanded, or nothing."""
    out = []
    for one in str(patch.get("Target") or "").split(","):
        one = one.strip().replace("\\", "/")
        if not one.lower().startswith("maps/"):
            continue
        name = one[5:]
        if "{{" in name:
            head, _, rest = name.partition("{{")
            token, _, tail = rest.partition("}}")
            if token.strip().lower() == "season":
                out.extend(head + s + tail for s in SEASONS)
            continue                 # any other token cannot be resolved from here
        out.append(name)
    return out


def scan_claims():
    """location name (lowercased) -> {mod folder that claims it}."""
    claims = collections.defaultdict(set)
    read = failed = 0
    for root in ROOTS:
        if not os.path.isdir(root):
            continue
        for category in sorted(os.listdir(root)):
            category_path = os.path.join(root, category)
            if not os.path.isdir(category_path):
                continue
            for mod in sorted(os.listdir(category_path)):
                mod_path = os.path.join(category_path, mod)
                if not os.path.isdir(mod_path):
                    continue
                # Walked in full. Map files sit up to seven folders below a mod's own folder in
                # this library, and a cap is how 568 of them went missing from the profile
                # generator - the same mistake would hide the mod that owns a place from here.
                for dirpath, dirnames, filenames in os.walk(mod_path):
                    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
                    for filename in filenames:
                        lower = filename.lower()
                        if lower.endswith(MAP_EXT):
                            claims[os.path.splitext(filename)[0].lower()].add(mod)
                        elif lower.endswith(".json") and lower not in ("manifest.json", "config.json"):
                            path = os.path.join(dirpath, filename)
                            try:
                                if os.path.getsize(path) > MAXIMUM_PATCH_BYTES:
                                    continue
                                text = open(path, encoding="utf-8-sig", errors="replace").read()
                            except OSError:
                                continue
                            if '"Target"' not in text or '"Changes"' not in text:
                                continue
                            try:
                                document = tolerant_json(text)
                                read += 1
                            except Exception:
                                failed += 1
                                continue
                            for patch in (document.get("Changes") or []):
                                if not isinstance(patch, dict):
                                    continue
                                for name in targets_of(patch):
                                    claims[name.lower()].add(mod)
    return claims, read, failed


def profile_mods():
    """profile name -> {mod folder enabled in it}."""
    out = {}
    if not os.path.isdir(PROFILE_DIR):
        return out
    for filename in sorted(os.listdir(PROFILE_DIR)):
        if not filename.endswith(".json"):
            continue
        try:
            with open(os.path.join(PROFILE_DIR, filename), encoding="utf-8") as handle:
                document = json.load(handle)
        except Exception:
            continue
        enabled = set()
        for mods in (document.get("enabled") or {}).values():
            enabled.update(mods)
        out[os.path.splitext(filename)[0]] = enabled
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--show", help="report one place instead of writing the table")
    parser.add_argument("--out", default=os.path.join(LABELER, "locowner.js"))
    arguments = parser.parse_args()

    claims, read, failed = scan_claims()
    print(f"read {read:,} patch file(s), {failed} unreadable; "
          f"{len(claims):,} place name(s) claimed by something")

    if arguments.show:
        who = sorted(claims.get(arguments.show.lower(), ()))
        print(f"\n{arguments.show}: claimed by {len(who)} pack(s)")
        for one in who:
            print(f"  {one}")
        if len(who) > 1:
            print("\nonly one of them won, and which is load order: this cannot say.")
        return 0

    dump_path = os.path.join(HFDIR, "maps.json")
    if not os.path.exists(dump_path):
        sys.exit(f"no dump at {dump_path}: run the sweep first")
    with open(dump_path, encoding="utf-8") as handle:
        locations = (json.load(handle).get("locations") or {})
    enabled_in = profile_mods()

    owners, single, contested, unclaimed = {}, 0, 0, 0
    for key, record in locations.items():
        name = (record.get("name") or key).lower()
        claimed_by = claims.get(name)
        if not claimed_by:
            unclaimed += 1
            continue
        # Only packs that were actually SWITCHED ON in a profile that produced this version
        # could have produced it. That is what turns "four packs patch Town" into an answer.
        here = set()
        for profile in (record.get("from") or []):
            here |= claimed_by & enabled_in.get(profile, set())
        if not here:
            unclaimed += 1
            continue
        owners[key] = sorted(here)
        if len(here) == 1:
            single += 1
        else:
            contested += 1

    header = ("// generated by tools/labelops/whoowns.py - which MOD each version of a place\n"
              "// came from, narrowed from the profiles that dumped it to the packs enabled in\n"
              "// those profiles that actually claim the place. More than one name means more\n"
              "// than one could have produced it and only load order decided which did.\n"
              "window.LOCOWNER = ")
    with open(arguments.out, "w", encoding="utf-8") as handle:
        handle.write(header)
        json.dump(owners, handle, ensure_ascii=False, indent=0)
        handle.write(";\n")
    print(f"\n{len(owners):,} version(s) attributed  ({single:,} to one pack, "
          f"{contested:,} to several)\n{unclaimed:,} claimed by nothing enabled: the base game's")
    print(f"written to {arguments.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
