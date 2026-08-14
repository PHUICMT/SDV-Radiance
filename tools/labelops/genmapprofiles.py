"""Colour the map mods into as few profiles as their conflicts allow, for full MAP coverage.

Two mods that Load the same Maps/ target cannot be enabled together - Content Patcher lets one
win and the loser's map never reaches the dump. So "every map mod, dumped" is a graph colouring:
one colour per profile, no two clashing mods sharing a colour. The floor is the biggest pile-up
on any single target, which the survey put at 34 (Maps/Greenhouse).

Only mods that SHIP MAPS are considered. Tilesheets need no profile at all - radiance_mapdump
all reads them off disk - so parking 300 sheet-only packs costs nothing and buys nothing here.
"""
import json, os, re, sys
from collections import defaultdict
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HERE = os.path.dirname(os.path.abspath(__file__))
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


def strip_jsonc(raw):
    out, i, n, ins, esc = [], 0, len(raw), False, False
    while i < n:
        c = raw[i]
        if ins:
            out.append(c)
            if esc: esc = False
            elif c == "\\": esc = True
            elif c == '"': ins = False
            i += 1; continue
        if c == '"':
            ins = True; out.append(c); i += 1; continue
        if c == "/" and i+1 < n and raw[i+1] == "/":
            while i < n and raw[i] != "\n": i += 1
            continue
        if c == "/" and i+1 < n and raw[i+1] == "*":
            j = raw.find("*/", i+2); i = n if j < 0 else j+2; continue
        out.append(c); i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def read_json(p):
    try:
        raw = open(p, encoding="utf-8-sig", errors="replace").read()
    except OSError:
        return None
    for attempt in (raw, strip_jsonc(raw)):
        try:
            return json.loads(attempt)
        except Exception:
            pass
    return None


index, by_id = {}, {}
loads = defaultdict(set)      # (cat, mod) -> Maps/ targets it LOADS
has_map = set()
for root in ROOTS:
    base = os.path.join(GAME, root)
    if not os.path.isdir(base):
        continue
    for cat in sorted(os.listdir(base)):
        cp = os.path.join(base, cat)
        if not os.path.isdir(cp) or cat in SKIP_CATS:
            continue
        for mod in sorted(os.listdir(cp)):
            mp = os.path.join(cp, mod)
            if not os.path.isdir(mp) or mod in BANNED:
                continue
            uids, deps = [], []
            key = (cat, mod)
            for dp, dns, fns in os.walk(mp):
                for fn in fns:
                    l = fn.lower()
                    if l.endswith((".tmx", ".tbin")):
                        has_map.add(key)
                    elif l == "manifest.json":
                        m = read_json(os.path.join(dp, fn))
                        if not m:
                            continue
                        if m.get("UniqueID"):
                            uids.append(m["UniqueID"])
                        for d in (m.get("Dependencies") or []):
                            if d.get("IsRequired") is not False and d.get("UniqueID"):
                                deps.append(d["UniqueID"])
                        cpf = m.get("ContentPackFor", {})
                        if isinstance(cpf, dict) and cpf.get("UniqueID"):
                            deps.append(cpf["UniqueID"])
                    elif l == "content.json":
                        try:
                            raw = open(os.path.join(dp, fn), encoding="utf-8-sig",
                                       errors="replace").read()
                        except OSError:
                            continue
                        for mm in re.finditer(
                                r'"Action"\s*:\s*"Load"[^}]{0,400}?"Target"\s*:\s*"([^"]+)"',
                                raw, re.I | re.S):
                            for t in mm.group(1).split(","):
                                t = t.strip()
                                if t.lower().startswith("maps/") and "{{" not in t:
                                    loads[key].add(t)
                if dp.count(os.sep) - mp.count(os.sep) >= 4:
                    dns[:] = []
            if not uids and key not in has_map:
                continue
            own = {u.lower() for u in uids}
            index[key] = {"uids": uids, "deps": sorted({d for d in deps if d.lower() not in own})}
            for u in uids:
                by_id.setdefault(u.lower(), key)

# Only map-shipping mods need colouring; the rest ride along in every profile.
players = sorted(k for k in index if k in has_map or loads.get(k))
owner = defaultdict(list)
for k in players:
    for t in loads.get(k, ()):
        owner[t].append(k)
adj = defaultdict(set)
for t, ms in owner.items():
    for i in range(len(ms)):
        for j in range(i + 1, len(ms)):
            adj[ms[i]].add(ms[j]); adj[ms[j]].add(ms[i])

# Greedy colouring, most-constrained first: that is what makes the count land on the floor.
order = sorted(players, key=lambda k: -len(adj[k]))
colour = {}
for k in order:
    used = {colour[n] for n in adj[k] if n in colour}
    c = 0
    while c in used:
        c += 1
    colour[k] = c
n_profiles = max(colour.values()) + 1 if colour else 1
print(f"{len(players)} map-shipping mods · {len(owner)} Maps/ targets · {n_profiles} profiles needed")


def resolve(sel):
    have = {u.lower() for k in sel for u in index[k]["uids"]}
    changed = True
    while changed:
        changed = False
        for k in list(sel):
            for d in index[k]["deps"]:
                dl = d.lower()
                if dl in have or dl not in by_id:
                    continue
                nk = by_id[dl]
                if nk not in sel:
                    sel.add(nk); have.update(u.lower() for u in index[nk]["uids"])
                    changed = True
    return sel


must = {k for k in MUST if k in index}
# Deliberately NOT "everything that is not a map mod". A sheet-only content pack contributes
# nothing to MAP mode and its art is read off disk regardless, so enabling 300 of them per pass
# only makes the game slower to load and gives Content Patcher 300 more chances to conflict.
# Each pass is the tooling, this pass's map mods, and whatever those two actually depend on.
written = []
for c in range(n_profiles):
    sel = set(must) | {k for k, v in colour.items() if v == c}
    sel = resolve(sel)
    enabled = defaultdict(list)
    for cat, mod in sorted(sel):
        enabled[cat].append(mod)
    name = f"MapPass-{c+1:02d}"
    doc = {"name": name, "created": "2026-08-13",
           "note": (f"Map-coverage pass {c+1} of {n_profiles}. Mods that Load the same map cannot "
                    f"be enabled together, so full MAP-mode coverage takes one pass per claimant "
                    f"of the most contested target. Dump each and merge. Tilesheets are NOT the "
                    f"reason for these passes - radiance_mapdump all reads those off disk."),
           "enabled": dict(enabled)}
    json.dump(doc, open(os.path.join(GAME, "mod-profiles", f"{name}.json"), "w",
                        encoding="utf-8"), ensure_ascii=False, indent=2)
    written.append((name, sum(len(v) for v in enabled.values()),
                    len(enabled.get("04_Maps-World", [])) + len(enabled.get("11_Maps", []))
                    + len(enabled.get("10_Locations", []))))

for nm, tot, maps in written:
    print(f"  {nm}: {tot} mods ({maps} map packs)")
json.dump([w[0] for w in written], open(os.path.join(HERE, "mappasses.json"), "w"), indent=1)
