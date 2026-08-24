"""Move the archives out of the HF-Studio folder so the Import dialog is usable.

    python tools/labelops/tidyhf.py            say what would move
    python tools/labelops/tidyhf.py --move     move it into _old/

The folder is the labeller's Live JSON folder, so it holds 286 `<sheet>.labels.json` files that
have to stay exactly where they are. What does not have to stay is the pile of dated backups
around them: eleven archive folders, seven old whole-dump copies, six palette variants of the
sheet art, and 120-odd per-pass maps-*.json files from the old pass naming. In a file dialog
those sort to the top and push everything else off the screen.

WHAT IS NEVER MOVED, because the tools read it by that exact path:
  maps.json and maps/       the dump and its 5,932 per-location files
  sheets/                   8,262 sheet art PNGs the map view draws from
  labels/ and *.labels.json the labels themselves
  label-packs/ fingerprints/ sweep-logs/   read by the labeller and the sweep tools
  modsheets.json vanilla-copies.json twin-suggestions.json   written for the labeller

Everything moved keeps its name inside _old/, so putting one back is a drag, not a restore.
"""
import argparse, os, shutil, sys

sys.stdout.reconfigure(encoding="utf-8")

HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
OLD = os.path.join(HFDIR, "_old")

# Never moved, whatever the patterns below say.
KEEP = {
    "maps.json", "maps", "sheets", "labels", "label-packs", "fingerprints", "sweep-logs",
    "modsheets.json", "vanilla-copies.json", "twin-suggestions.json", "recolour-pairs.json",
    "missing-dependencies.json", "mod-profiles", "_old",
    ".acc-index.json", ".acc-done.json", ".acc-maps",
}


def is_archive(name):
    """Whether an entry is a dated backup, an old dump, or an art variant."""
    if name in KEEP or name.endswith(".labels.json"):
        return False
    lower = name.lower()
    if lower.startswith("!") or lower.endswith((".log", ".labels-import.json")):
        return False
    if name.startswith("_") or ".pre-" in name:
        return True
    if lower.startswith(("maps-mappass", "maps-label-wide", "sheets.")):
        return True
    # Every other maps.* is an older copy of the dump or an index from a run that is over:
    # maps.json.before-casefix, maps.merged2902-index.json, maps.old-passes. maps.json itself
    # and the maps/ folder are in KEEP, so this cannot take the live ones.
    if lower.startswith("maps.") or lower.startswith("maps-"):
        return True
    if lower.startswith("restore-") or lower.endswith((".png", ".bak", ".tsv")):
        return True
    if name in ("base-art.json", "sheet-versions.json",
                "radiance-labels-AUTO.json", "radiance-labels-REBUILT.json",
                "spring_town-restore.json", "spring_town-restore-reflect_floor.json"):
        return True
    return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--move", action="store_true")
    arguments = parser.parse_args()

    moving = sorted(name for name in os.listdir(HFDIR) if is_archive(name))
    staying = sorted(name for name in os.listdir(HFDIR) if not is_archive(name))
    labels = sum(1 for name in staying if name.endswith(".labels.json"))

    print(f"{len(moving)} entr(ies) to move into _old/")
    for name in moving[:14]:
        print("   ", name)
    if len(moving) > 14:
        print(f"    ... and {len(moving) - 14} more")
    print()
    print(f"{len(staying)} staying ({labels} of them label files the labeller writes)")
    for name in staying:
        if not name.endswith(".labels.json"):
            print("   ", name)

    if not arguments.move:
        print("\npass --move to do it")
        return

    os.makedirs(OLD, exist_ok=True)
    moved = failed = 0
    for name in moving:
        source = os.path.join(HFDIR, name)
        target = os.path.join(OLD, name)
        try:
            if os.path.exists(target):
                print(f"    already in _old, left alone: {name}")
                continue
            shutil.move(source, target)
            moved += 1
        except OSError as problem:
            failed += 1
            print(f"    could not move {name}: {problem}")
    print(f"\nmoved {moved}, failed {failed}  ->  {OLD}")


if __name__ == "__main__":
    main()
