"""Generate N labelling profiles that between them load EVERY map mod on disk.

Mods that Load the same Maps/ target cannot share a profile, so the conflict clusters decide
how many profiles are needed: one per member of the largest cluster. Everything that clashes
with nobody goes in all of them, so each profile is a full, playable set.
"""
import json, os, re, sys
from collections import defaultdict
sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
sys.path.insert(0, HERE)
from conflicts import read_json  # noqa: E402  (reuses the string-aware jsonc reader)

CONF = json.load(open(os.path.join(HERE, "conflicts.json"), encoding="utf-8"))
clusters = [[tuple(m) for m in c] for c in CONF["clusters"]]

# --- index every mod folder + its manifest dependencies ---
index, by_id = {}, {}
for root in ["Mods", "Mods (disabled)"]:
    base = os.path.join(GAME, root)
    if not os.path.isdir(base):
        continue
    for cat in sorted(os.listdir(base)):
        cp = os.path.join(base, cat)
        if not os.path.isdir(cp):
            continue
        for mod in sorted(os.listdir(cp)):
            mp = os.path.join(cp, mod)
            if not os.path.isdir(mp):
                continue
            # EVERY manifest under the folder, not the first one found. A big pack ships as a
            # bundle of sub-packs nested several levels down (Ridgeside Village keeps its CP,
            # SMAPI, Custom Companions and FTM components at
            # "Ridgeside Village\Ridgeside Village\[CP] Ridgeside Village\"), and each declares
            # its own dependencies. Reading only the outermost manifest missed Custom
            # Companions entirely, so the profile loaded a Ridgeside that refused to install.
            # This is the same blind spot sdv-mods.ps1 has.
            mans = []
            for dp, dns, fns in os.walk(mp):
                if "manifest.json" in fns:
                    m2 = read_json(os.path.join(dp, "manifest.json"))
                    if m2:
                        mans.append(m2)
                if dp.count(os.sep) - mp.count(os.sep) >= 4:
                    dns[:] = []
            if not mans:
                continue
            deps, uids = [], []
            for man in mans:
                for d in (man.get("Dependencies") or []):
                    if d.get("IsRequired") is not False and d.get("UniqueID"):
                        deps.append(d["UniqueID"])
                cpf = man.get("ContentPackFor", {})
                if isinstance(cpf, dict) and cpf.get("UniqueID"):
                    deps.append(cpf["UniqueID"])
                if man.get("UniqueID"):
                    uids.append(man["UniqueID"])
            # A bundle SATISFIES its own sub-packs, so drop self-provided ids from the wants.
            own = {u.lower() for u in uids}
            rec = dict(cat=cat, folder=mod, uid=uids[0] if uids else "", uids=uids,
                       deps=sorted({d for d in deps if d.lower() not in own}))
            index[(cat, mod)] = rec
            for u in uids:
                by_id.setdefault(u.lower(), rec)

# --- what belongs in the labelling set at all ---
SKIP_CATS = {"01_Characters-Anime", "06_Localization", "07_NSFW", "08_Misc-Fun", "05_Gameplay"}
# Never load these next to Radiance whatever their content says: the first two draw their own
# post-process over the same frame (the orange/black loading screen and the dead patches at the
# water mirror), and Height Framework is the mod whose job Radiance absorbed - it was pulled
# from every profile on purpose and re-enabling it puts the old deck veto back in play.
BANNED_IDS = {"cat.dynamicshader", "cat.dynamicreflections", "cat.smoothcamera",
              "phuicmt.heightframework", "sdv.heightframework"}
BANNED_FOLDERS = {"DynamicShader", "DynamicReflections", "SmoothCamera", "HeightFramework"}


def has_map_content(mod_dir):
    """Does this mod actually contribute a MAP or a TILESHEET the labeller would ever show?

    Name prefixes were too blunt: a wardrobe pack written with a fullwidth bracket slipped
    straight past a "[FS]" test. Ask the folder instead - a map file, a Maps/ patch target, or
    art living under a maps/tilesheets path. Character sprites match none of the three.
    """
    for dp, dns, fns in os.walk(mod_dir):
        # RELATIVE to the mod folder, never the absolute path: the install lives under
        # "steamapps", which contains the substring "map", so an absolute-path test matched
        # every png of every wardrobe pack on the machine and let 60 of them into the profile.
        low = os.path.relpath(dp, mod_dir).lower()
        for fn in fns:
            l = fn.lower()
            if l.endswith((".tmx", ".tbin")):
                return True
            if l == "content.json":
                try:
                    raw = open(os.path.join(dp, fn), encoding="utf-8-sig").read()
                except Exception:
                    continue
                if re.search(r'"Target"\s*:\s*"[^"]*Maps/', raw, re.I):
                    return True
            if l.endswith(".png") and ("map" in low or "tilesheet" in low or "tilesheets" in low):
                return True
    return False


def wanted(cat, mod):
    if cat in SKIP_CATS or mod in BANNED_FOLDERS:
        return False
    rec = index.get((cat, mod))
    if rec and rec["uid"].lower() in BANNED_IDS:
        return False
    return has_map_content(os.path.join(GAME, "Mods", cat, mod)) or \
        has_map_content(os.path.join(GAME, "Mods (disabled)", cat, mod))


base_set = {k for k in index if wanted(*k)}
# The tooling itself carries no map, so the content test throws it out - and without Radiance
# there is no radiance_mapdump and without AgentBridge nothing can drive it. Config UI and the
# texture frameworks are here because the map packs below expect them at load time.
MUST = [("03_Graphics-FX", "SDV-Radiance"), ("00_Frameworks", "SDV-AgentBridge"),
        ("00_Frameworks", "ContentPatcher"), ("00_Frameworks", "ConsoleCommands"),
        ("00_Frameworks", "GenericModConfigMenu"), ("00_Frameworks", "AlternativeTextures"),
        ("00_Frameworks", "SpaceCore"), ("00_Frameworks", "FarmTypeManager"),
        ("00_Frameworks", "SolidFoundations"), ("00_Frameworks", "SaveBackup")]
for k in MUST:
    if k in index:
        base_set.add(k)
    else:
        print(f"note: required tool {k[0]}/{k[1]} not on disk")
# The same mod installed TWICE under two categories is a duplicate install, not a conflict:
# both copies carry one UniqueID and SMAPI loads one and warns about the other. Keep a single
# copy (the specific category over the generic framework bucket) so it is never rotated out of
# a profile as though it clashed with itself.
dupes = defaultdict(list)
for k in base_set:
    if index[k]["uid"]:
        dupes[index[k]["uid"].lower()].append(k)
for uid, ks in dupes.items():
    if len(ks) < 2:
        continue
    keep = sorted(ks, key=lambda k: (k[0] == "00_Frameworks", k[0]))[0]
    for k in ks:
        if k != keep:
            base_set.discard(k)
    print(f"duplicate install {index[keep]['folder']}: keeping {keep[0]}, dropping "
          + ", ".join(k[0] for k in ks if k != keep))
# SVE and Stardew Valley Reimagined 3 both overhaul the vanilla maps. CP does not report it as
# a Load clash because SVR3 edits rather than loads, but the result is still one map with two
# authors' art fighting over it, which is not a scene worth labelling.
EXCLUDE = {("03_Graphics-FX", "Stardew Valley Reimagined 3")}
base_set -= EXCLUDE

clustered = {m for c in clusters for m in c}
free = sorted(base_set - clustered)
in_clusters = [sorted(set(c) & base_set) for c in clusters]
in_clusters = [c for c in in_clusters if c]
n = max((len(c) for c in in_clusters), default=1)
print(f"{len(base_set)} mods wanted · {len(free)} conflict-free · "
      f"{len(in_clusters)} clusters · {n} profiles needed")


def resolve(sel):
    """Pull in every required dependency that exists on disk."""
    have = {index[k]["uid"].lower() for k in sel if index[k]["uid"]}
    changed = True
    while changed:
        changed = False
        for k in list(sel):
            for dep in index[k]["deps"]:
                d = dep.lower()
                if d in have or d not in by_id:
                    continue
                rec = by_id[d]
                nk = (rec["cat"], rec["folder"])
                if nk in sel:
                    continue
                # a dependency comes in even from a skipped category: without it the
                # dependent mod simply refuses to load and its maps never reach the dump
                sel.add(nk)
                have.add(d)
                changed = True
    return sel


out = []
for i in range(n):
    sel = set(free)
    picked = {}
    for c in in_clusters:
        m = c[i % len(c)]
        sel.add(m)
        picked[m[1]] = True
    sel = resolve(sel)
    enabled = defaultdict(list)
    for cat, mod in sorted(sel):
        enabled[cat].append(mod)
    name = f"Label-Wide-{i+1}"
    maps = len(enabled.get("04_Maps-World", []))
    doc = {
        "name": name,
        "created": "2026-08-13",
        "note": (f"Labelling profile {i+1} of {n}. Together these cover every map mod on disk. "
                 f"{sum(len(v) for v in enabled.values())} mods, {maps} in 04_Maps-World. "
                 f"Rotating picks this round: {', '.join(sorted(picked))}. "
                 f"Dump each profile in turn and merge the results - a location only reaches the "
                 f"dump if its mod is loaded, while TILESHEET art is read off disk by "
                 f"radiance_mapdump all and so needs no profile at all."),
        "enabled": dict(enabled),
    }
    dest = os.path.join(GAME, "mod-profiles", f"{name}.json")
    json.dump(doc, open(dest, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    out.append((name, sum(len(v) for v in enabled.values()), maps, sorted(picked)))
    print(f"  wrote {name}: {out[-1][1]} mods ({maps} map mods) · picks {sorted(picked)}")

print("\nrotating members per cluster:")
for c in in_clusters:
    print("  " + " | ".join(m[1] for m in c))
