"""Do the newly downloaded mods need extra dump PASSES, or does one dump cover them?

Two different questions, and they have different answers:
  SHEET mode - radiance_mapdump all reads every PNG off disk, parked folders included, so a
               tilesheet needs no profile and no pass. One dump, whatever is enabled.
  MAP mode   - a location only reaches the dump if the game LOADED it, so a mod that ships
               .tmx/.tbin maps has to be enabled, and mods that replace the same map cannot
               be enabled together.

So the cost of full coverage is decided by how many of them ship MAPS, and how much those
maps collide. Counted here rather than assumed.
"""
import os, re, sys
from collections import Counter, defaultdict
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PARK = os.path.join(GAME, "Mods (disabled)")
CATS = ["09_Buildings", "10_Locations", "11_Maps", "12_Visuals"]
NOT_PLACES = ("portrait", "character", "animals", "fashion", "\\ui", "icon", "emoji",
              "hair", "shirt", "pants", "hats", "shoes", "tattoo", "bodies",
              "\\shots\\", "screenshot", "\\shot_")


def survey(mod_dir):
    sheets = maps = 0
    loads = set()
    head = bytearray(24)
    for dp, _dn, fns in os.walk(mod_dir):
        rel = ("\\" + os.path.relpath(dp, mod_dir).lower() + "\\").replace("/", "\\")
        skip = any(b in rel for b in NOT_PLACES)
        for fn in fns:
            l = fn.lower()
            if l.endswith((".tmx", ".tbin")):
                maps += 1
            elif l == "content.json":
                try:
                    raw = open(os.path.join(dp, fn), encoding="utf-8-sig",
                               errors="replace").read()
                except OSError:
                    continue
                # every Load of a Maps/ target: that is the exclusive claim on a location
                for m in re.finditer(r'"Action"\s*:\s*"Load"[^}]{0,400}?"Target"\s*:\s*"([^"]+)"',
                                     raw, re.I | re.S):
                    for t in m.group(1).split(","):
                        t = t.strip()
                        if t.lower().startswith("maps/") and "{{" not in t:
                            loads.add(t)
            elif l.endswith(".png") and not skip:
                try:
                    with open(os.path.join(dp, fn), "rb") as fh:
                        if fh.readinto(head) < 24 or head[1:4] != b"PNG":
                            continue
                    w = int.from_bytes(head[16:20], "big")
                    h = int.from_bytes(head[20:24], "big")
                    if w >= 128 and h >= 128 and w % 16 == 0 and h % 16 == 0:
                        sheets += 1
                except OSError:
                    pass
    return sheets, maps, loads


tally = Counter()
owners = defaultdict(list)
sheet_only = with_maps = nothing = 0
total_sheets = 0
for cat in CATS:
    root = os.path.join(PARK, cat)
    if not os.path.isdir(root):
        continue
    for mod in sorted(os.listdir(root)):
        p = os.path.join(root, mod)
        if not os.path.isdir(p):
            continue
        s, m, loads = survey(p)
        total_sheets += s
        if m:
            with_maps += 1
            tally[cat + " with maps"] += 1
        elif s:
            sheet_only += 1
            tally[cat + " sheets only"] += 1
        else:
            nothing += 1
        for t in loads:
            owners[t].append(mod)

print(f"{sheet_only + with_maps + nothing} mods surveyed\n")
print(f"  sheets only, NO map   : {sheet_only:4d}   <- one dump covers these, no profile needed")
print(f"  ships .tmx/.tbin maps : {with_maps:4d}   <- needs enabling to reach MAP mode")
print(f"  neither               : {nothing:4d}")
print(f"  labellable sheets     : {total_sheets:,}\n")

clash = {t: v for t, v in owners.items() if len(v) > 1}
print(f"map targets claimed by more than one mod: {len(clash)}")
for t, v in sorted(clash.items(), key=lambda kv: -len(kv[1]))[:12]:
    print(f"  {t:38s} {len(v)} mods")
if clash:
    worst = max(len(v) for v in clash.values())
    print(f"\nbiggest pile-up on one map: {worst} mods")
    print(f"-> full MAP coverage of these would need {worst} passes, one per claimant")
