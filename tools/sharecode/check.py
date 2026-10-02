"""The share-code registry has to stay a promise, so this refuses a build that breaks one.

A share code carries each setting under a number. The number is assigned once and never reused,
because somebody else's saved code is read by that number years later. Three ways to break that,
all of them silent without this check:

  * a setting added to the look and never given a number, so codes quietly lose it
  * one number on two settings, so a code sets the wrong dial
  * a number pointing at a property that no longer exists, so the entry is dropped

Run on its own, or as a step of tools/build-check.sh:

    python tools/sharecode/check.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CONFIG = os.path.join(REPO, "src", "Config", "ModConfig.cs")
SHARECODE = os.path.join(REPO, "src", "Config", "ShareCode.cs")

# Mirrors ModConfig.TunableProps: a share code carries the same kinds a saved look does, plus the
# one string that is part of a look (the colour table's name).
CARRIED_TYPES = {"bool", "int", "float"}
# Every enum declared anywhere in the mod, found rather than listed: a hand-kept list missed the
# two Smooth art kernel enums, so six kernel settings travelled in no code and this check passed.
def declared_enums():
    found = set()
    pattern = re.compile(r"\benum\s+([A-Za-z0-9_]+)")
    for folder, _, files in os.walk(os.path.join(REPO, "src")):
        for file in files:
            if file.endswith(".cs"):
                with open(os.path.join(folder, file), encoding="utf-8") as source:
                    found.update(pattern.findall(source.read()))
    return found


ENUM_TYPES = declared_enums()
CARRIED_STRINGS = {"ColorGradeLut"}

# Carried by no code at all. Bookkeeping that means nothing on somebody else's install, plus the
# keys, which travel nowhere because a key that fights another mod's key does it invisibly.
# ActivePerfPreset is only a label saying which button was last pressed; the four settings it moves
# are in the registry themselves, so the label would say nothing the values do not.
CARRIED_BY_NO_CODE = {
    "ActivePerfPreset", "DebugLogging", "SavedProfiles", "TunerFoldedSections", "TunerLastTab",
    "TunerShowFineTuning", "ConfigVersion", "ActivePreset", "Enabled", "WindowCompatAppliedFor",
    "WaterDisabledLocations", "ToggleKey", "TunerKey", "InspectDrawKey",
}

# The performance group. A look code leaves these behind; a whole-setup code carries them and the
# person receiving one is shown them apart from the look, because this is the one group that can
# make a game feel worse rather than look different.
PERFORMANCE = {"LimitSamplerSlots", "RenderScale", "RenderScaleAuto", "RenderSharpness"}

# The colour tables a code names by their PLACE in this list rather than by their name, which is a
# byte instead of a dozen. The list is therefore append-only forever: inserting a name in the middle
# would re-point every code anybody has already shared at the wrong table.
SHIPPED_LUTS_FROZEN = ["", "warm-film", "verdant", "autumn-gold", "moonlit", "cool-night",
                       "washed-linen", "identity"]


def carried_settings():
    text = open(CONFIG, encoding="utf-8").read()
    body = text[text.index("public sealed class ModConfig"):]
    found = []
    for kind, name in re.findall(r"public\s+([A-Za-z0-9_<>\[\]\?]+)\s+([A-Za-z0-9_]+)\s*\{\s*get;\s*set;\s*\}", body):
        if name in CARRIED_BY_NO_CODE:
            continue
        if kind in CARRIED_TYPES or kind in ENUM_TYPES or (kind == "string" and name in CARRIED_STRINGS):
            found.append(name)
    return found


def registry():
    text = open(SHARECODE, encoding="utf-8").read()
    start = text.index("private static readonly (int Id, string Property, bool IsPerformance)[] Registry")
    end = text.index("];", start)
    return [(int(number), name, group)
            for number, name, group in re.findall(r"\((\d+),\s*nameof\(ModConfig\.([A-Za-z0-9_]+)\),\s*(Look|Performance)\)",
                                       text[start:end])]


def main():
    settings = carried_settings()
    rows = registry()
    complaints = []

    seen_numbers = {}
    seen_properties = {}
    for number, name, group in rows:
        if number in seen_numbers:
            complaints.append(f"number {number} is on both {seen_numbers[number]} and {name}: "
                              "a number belongs to one setting forever")
        seen_numbers[number] = name
        if name in seen_properties:
            complaints.append(f"{name} has two numbers, {seen_properties[name]} and {number}")
        seen_properties[name] = number
        wanted_group = "Performance" if name in PERFORMANCE else "Look"
        if group != wanted_group:
            complaints.append(f"{name} is filed as {group} and belongs in {wanted_group}")

    for name in settings:
        if name not in seen_properties:
            complaints.append(f"{name} is carried by a code and has no number. Give it the next unused "
                              f"number ({max(seen_numbers) + 1 if seen_numbers else 0}) at the END of "
                              "the registry, never a number that was used before")

    known = set(settings)
    for number, name, _ in rows:
        if name not in known:
            complaints.append(f"number {number} points at {name}, which is no longer a look setting. "
                              "Leave the number out rather than handing it to something else")

    shipped = re.search(r"ShippedLuts\s*=\s*\[([^\]]*)\]", open(CONFIG, encoding="utf-8").read())
    names = re.findall(r'"([^"]*)"', shipped.group(1) if shipped else "")
    if names[:len(SHIPPED_LUTS_FROZEN)] != SHIPPED_LUTS_FROZEN:
        complaints.append("ShippedLuts has been reordered. Codes name a colour table by its place "
                          "in that list, so the list only ever grows at the end: "
                          f"expected it to start {SHIPPED_LUTS_FROZEN}, it starts {names[:len(SHIPPED_LUTS_FROZEN)]}")

    if complaints:
        print("share-code registry problems:")
        for line in complaints:
            print("  -", line)
        return 1
    looks = sum(1 for _, _, group in rows if group == "Look")
    print(f"share-code registry: {looks} look settings and {len(rows) - looks} performance, "
          f"numbers 0 to {max(seen_numbers)}, none shared")
    return 0


if __name__ == "__main__":
    sys.exit(main())
