r"""Every numeric setting must have one range: what ModConfig.Clamp enforces, what the GMCM page
offers and what the F6 tuner slider offers. Prints each disagreement and exits 1 when there is one.

Three hand-written copies of 189 ranges had drifted in two places for two months: the tuner's
flood strength slider ran to 1.5 while Clamp cut the saved value back to 1.0, and its cloud speed
slider stopped at 0.06 where the menu and Clamp allow 0.1. A [Range] attribute read by all three
would have meant touching every property; this check keeps the copies honest instead.

    python tools/check-setting-ranges.py
"""
import pathlib
import re
import sys

source = pathlib.Path(__file__).resolve().parent.parent / "src"
config_text = (source / "Config" / "ModConfig.cs").read_text(encoding="utf-8-sig")
gmcm_text = (source / "Entry" / "GmcmRegistration.cs").read_text(encoding="utf-8-sig")
tuner_text = (source / "UI" / "RadianceTunerMenu.cs").read_text(encoding="utf-8-sig")

constants = {
    match.group(1): float(match.group(2))
    for match in re.finditer(r"const\s+(?:float|int)\s+(\w+)\s*=\s*(-?[\d.]+)f?\s*;", config_text)
}


def number(token):
    token = re.sub(r"^ModConfig\.", "", token.strip())
    if token in constants:
        return constants[token]
    try:
        return float(token.rstrip("fF"))
    except ValueError:
        return None


clamped = {}
for pattern in (r"(\w+)\s*=\s*ClampToRange\(\s*\1\s*,\s*([^,]+),\s*([^)]+)\)",
                r"(\w+)\s*=\s*Math\.Clamp\(\s*\1\s*,\s*([^,]+),\s*([^)]+)\)"):
    for match in re.finditer(pattern, config_text):
        clamped.setdefault(match.group(1), (number(match.group(2)), number(match.group(3))))
# Compass bearings are wrapped onto 0..360 rather than clamped; the menu offers the whole turn.
wrapped = set(re.findall(r"(\w+)\s*=\s*WrapDegrees\(\s*\1\s*\)", config_text))

menu = {}
for match in re.finditer(r"AddNumberOption\(manifest,\s*\(\)\s*=>\s*config\(\)\.(\w+).*?\),\s*"
                         r"(?:null|\(\)\s*=>\s*translate\([^)]*\)),\s*([^,]+),\s*([^,)]+)", gmcm_text, re.S):
    menu.setdefault(match.group(1), (number(match.group(2)), number(match.group(3))))

tuner = {}
for match in re.finditer(r"Slider\(\"[^\"]+\",\s*([^,]+),\s*([^,]+),\s*\(\)\s*=>\s*_config\.(\w+)", tuner_text):
    tuner.setdefault(match.group(3), (number(match.group(1)), number(match.group(2))))

problems = []
for name in sorted(set(clamped) | set(menu) | set(tuner)):
    if name in wrapped:
        continue
    offered = {where: found[name] for where, found in (("menu", menu), ("tuner", tuner)) if name in found}
    if name not in clamped:
        problems.append(f"{name}: offered by {', '.join(offered)} but never clamped")
        continue
    for where, range_offered in offered.items():
        if range_offered != clamped[name]:
            problems.append(f"{name}: Clamp {clamped[name]} but the {where} offers {range_offered}")

print(f"setting ranges: {len(clamped)} clamped, {len(menu)} on the menu, {len(tuner)} in the tuner, "
      f"{len(wrapped)} wrapped bearings")
for problem in problems:
    print("  " + problem)
sys.exit(1 if problems else 0)
