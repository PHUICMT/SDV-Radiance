"""The tuner's heading and switch keys are names players' settings are kept under, so this refuses a
build that drops one.

A player who hides a group or a switch in F6 (2.4.2), or folds a group, has it remembered in
config.json under that heading's or switch's i18n key. Rename a key and what they hid comes back,
silently. So every key that has ever been a heading or a switch is listed in keys.txt, and a build
whose menu no longer has one fails here, unless ModConfig.RenamedTunerKeys says where it went (and
carries the player's entries there).

Run on its own, or as a step of tools/build-check.sh:

    python tools/tunerkeys/check.py            # check
    python tools/tunerkeys/check.py --update   # add keys the menu gained to keys.txt
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
MENU = os.path.join(REPO, "src", "UI", "RadianceTunerMenu.cs")
CONFIG = os.path.join(REPO, "src", "Config", "ModConfig.cs")
KEYS = os.path.join(HERE, "keys.txt")


def keys_in_menu():
    source = open(MENU, encoding="utf-8").read()
    found = set(re.findall(r'\bSection\("([a-z0-9.]+)"', source))
    found |= set(re.findall(r'\bToggle\("([a-z0-9.]+)"', source))
    # Each particle kind's group and switch are named from the kind (see Emitter).
    for kind in re.findall(r'\bEmitter\("([a-z0-9]+)"', source):
        found |= {f"tuner.section.particle{kind}", f"tuner.particle{kind}"}
    return found


def renamed():
    source = open(CONFIG, encoding="utf-8").read()
    block = re.search(r"RenamedTunerKeys\s*=\s*new\(\)\s*\{(.*?)\};", source, re.S)
    return dict(re.findall(r'\["([a-z0-9.]+)"\]\s*=\s*"([a-z0-9.]+)"', block.group(1))) if block else {}


def main():
    now = keys_in_menu()
    listed = [line.strip() for line in open(KEYS, encoding="utf-8") if line.strip() and not line.startswith("#")]
    moves = renamed()
    gone = [key for key in listed if key not in now and key not in moves]
    if "--update" in sys.argv:
        added = sorted(now - set(listed))
        with open(KEYS, "a", encoding="utf-8") as out:
            for key in added:
                out.write(key + "\n")
        listed += added
        print(f"keys.txt: {len(added)} added")
    if gone:
        print("These headings or switches are gone from the menu, and a player may have hidden or folded them:")
        for key in gone:
            print("   " + key)
        print("Put the key back, or add [\"old\"] = \"new\" to ModConfig.RenamedTunerKeys so their settings follow it.")
        return 1
    bad = [f"{old} -> {new}" for old, new in moves.items() if new not in now]
    if bad:
        print("RenamedTunerKeys points at keys the menu does not have: " + ", ".join(bad))
        return 1
    new = sorted(now - set(listed))
    if new:
        print(f"{len(new)} new key(s) not in keys.txt yet; run with --update: " + ", ".join(new[:8]))
        return 1
    print(f"{len(listed)} heading and switch keys, none lost")
    return 0


if __name__ == "__main__":
    sys.exit(main())
