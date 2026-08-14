"""Switch mod profiles by moving folders between Mods/<cat> and Mods (disabled)/<cat>.

Written in python on purpose: sdv-mods.ps1 does the same job with Copy-Item, which reads the
square brackets in folder names like "[A_TK] - Farm Project" as a wildcard character class and
silently moves nothing. Every path here is used literally.

    python applyprofile.py <ProfileName> [--dry]
"""
import json, os, shutil, sys
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
ON, OFF = os.path.join(GAME, "Mods"), os.path.join(GAME, "Mods (disabled)")


def scan():
    """(cat, folder) -> which root it currently sits in."""
    where = {}
    for root in (ON, OFF):
        if not os.path.isdir(root):
            continue
        for cat in os.listdir(root):
            cp = os.path.join(root, cat)
            if not os.path.isdir(cp):
                continue
            for mod in os.listdir(cp):
                if os.path.isdir(os.path.join(cp, mod)):
                    where[(cat, mod)] = root
    return where


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    name, dry = sys.argv[1], "--dry" in sys.argv
    prof = json.load(open(os.path.join(GAME, "mod-profiles", f"{name}.json"), encoding="utf-8-sig"))
    want = {(cat, mod) for cat, mods in prof["enabled"].items() for mod in mods}

    where = scan()
    before = len(where)
    missing = sorted(want - set(where))
    if missing:
        print(f"NOT ON DISK ({len(missing)}) - profile lists them but they do not exist:")
        for c, m in missing:
            print(f"  {c}/{m}")

    to_on = [k for k in want if where.get(k) == OFF]
    to_off = [k for k in where if k not in want and where[k] == ON]
    print(f"profile {name}: {len(want)} wanted · enable {len(to_on)} · disable {len(to_off)}")
    if dry:
        for c, m in sorted(to_on):
            print(f"  ON  {c}/{m}")
        for c, m in sorted(to_off):
            print(f"  off {c}/{m}")
        return

    moved = 0
    for src_root, dst_root, items in ((OFF, ON, to_on), (ON, OFF, to_off)):
        for cat, mod in items:
            src = os.path.join(src_root, cat, mod)
            dstdir = os.path.join(dst_root, cat)
            os.makedirs(dstdir, exist_ok=True)
            dst = os.path.join(dstdir, mod)
            if os.path.exists(dst):
                print(f"  SKIP {cat}/{mod}: already exists at destination")
                continue
            shutil.move(src, dst)
            moved += 1

    after = scan()
    if len(after) != before:
        sys.exit(f"MOD COUNT CHANGED {before} -> {len(after)}: something was lost, stop and check")
    on_now = sum(1 for k, v in after.items() if v == ON)
    print(f"moved {moved} folders · {on_now} mods now enabled · total unchanged at {before}")


if __name__ == "__main__":
    main()
