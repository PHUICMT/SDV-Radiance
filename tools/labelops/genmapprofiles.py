"""Colour the map mods into as few profiles as their clashes allow, for full coverage.

Two mods that clash cannot be enabled together, so "every mod, dumped" is a graph colouring:
one colour per profile, no two clashing mods sharing a colour. The floor is the biggest
pile-up on any one target. TWO kinds of clash count, for two different reasons.

  Action:Load on a Maps/ target is a COVERAGE failure. Content Patcher lets one mod win and
  every other claimant's map is never dumped at all. Worst pile-up: 73 packs on Greenhouse.

  Action:EditImage on a Maps/ sheet is an ATTRIBUTION failure, and attribution is what this
  whole exercise is for. Every patch applies, so the sheet that gets dumped is a blend of up
  to 75 packs - a picture nobody running one recolour will ever see, and so a picture whose
  labels help nobody. Worst pile-up: 75 packs on spring_outdoorsTileSheet.

  Action:EditMap is deliberately NOT coloured on, though 49 packs pile onto Town. It changes
  which tile goes where, not what a tile looks like, and a label belongs to (sheet, tile
  index, art). A composed map still draws real tiles whose art is attributable, so colouring
  on it would raise the pass count and buy nothing.

Only mods that SHIP MAPS or PATCH MAP ART are considered. A pack that does neither needs no
pass: radiance_mapdump reads unplaced tilesheets off disk regardless.
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


SEASONS = ("spring", "summer", "fall", "winter")


def season_targets(target):
    """Every Maps/ target one Target string means, {{season}} expanded.

    Expanded rather than skipped: the season token is how most recolours are written, and
    dropping those lines is how a pack that repaints all four towns looked like a pack that
    repaints nothing. Any other token still cannot be resolved from outside the game.
    """
    out = []
    for one in str(target or "").split(","):
        one = one.strip().replace("\\", "/")
        if not one.lower().startswith("maps/"):
            continue
        name = one[5:]
        if "{{" in name:
            head, _, rest = name.partition("{{")
            token, _, tail = rest.partition("}}")
            if token.strip().lower() == "season":
                out.extend("Maps/" + head + s + tail for s in SEASONS)
            continue
        out.append("Maps/" + name)
    return out


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
loads = defaultdict(set)
# EditImage claims, kept apart from Load: they clash for a different reason.
repaints = defaultdict(set)      # (cat, mod) -> Maps/ targets it LOADS
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
                # No depth cap. Map files sit up to SEVEN folders below a mod's own folder here,
                # and the cap that used to stop at four hid 568 of them - which decided, wrongly,
                # that their mods ship no maps and need no profile, so those maps were never
                # dumped at all. Only folders that cannot hold mod content are skipped.
                dns[:] = [d for d in dns if d not in (".git", "node_modules", "__pycache__")]
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
                    elif l.endswith(".json") and l not in ("manifest.json", "config.json"):
                        path = os.path.join(dp, fn)
                        try:
                            if os.path.getsize(path) > 8 * 1024 * 1024:
                                continue
                            raw = open(path, encoding="utf-8-sig", errors="replace").read()
                        except OSError:
                            continue
                        if '"Target"' not in raw or '"Changes"' not in raw:
                            continue
                        # Parsed, not pattern-matched. The regex this replaces required Action to
                        # come before Target within 400 characters of it, which is a house style
                        # and not a rule: a pack that writes Target first, or puts a long When
                        # block between them, declared nothing as far as this was concerned. It
                        # also only ever looked at content.json, so a pack that splits its patch
                        # list across several files was invisible.
                        try:
                            doc = json.loads(strip_jsonc(raw))
                        except Exception:
                            continue
                        for patch in (doc.get("Changes") or []):
                            if not isinstance(patch, dict):
                                continue
                            action = str(patch.get("Action") or "").lower()
                            if action not in ("load", "editimage"):
                                continue
                            for t in season_targets(patch.get("Target")):
                                (loads if action == "load" else repaints)[key].add(t)
            if not uids and key not in has_map:
                continue
            own = {u.lower() for u in uids}
            index[key] = {"uids": uids, "deps": sorted({d for d in deps if d.lower() not in own})}
            for u in uids:
                by_id.setdefault(u.lower(), key)

# A mod needs a pass of its own if it ships maps, claims a map, or repaints map ART. The last
# is new: a recolour ships no map at all and was therefore never coloured, so every pass had
# whichever recolours happened to be enabled blended into its sheets.
players = sorted(k for k in index if k in has_map or loads.get(k) or repaints.get(k))


def closure(k):
    """The mod plus everything it drags in, transitively. Same set resolve() will build."""
    seen, stack = {k}, [k]
    while stack:
        at = stack.pop()
        for d in index.get(at, {}).get("deps", ()):
            n = by_id.get(d.lower())
            if n and n not in seen:
                seen.add(n); stack.append(n)
    return seen


# WHAT A MOD CLAIMS IS WHAT ITS CLOSURE CLAIMS.
#
# The colouring used to look at each mod alone, and resolve() then added the dependencies to the
# chosen pass without asking what colour they were. Measured on the 101 profiles that produced:
# THIRTY of them held two mods claiming one target. Stardew Valley Expanded is dragged into 25
# closures and claims 290 targets, so wherever it lands it takes those targets from the mods that
# pass exists to dump - and the pass still runs, still reports success, and the map it was for is
# quietly absent from a corpus that looks complete.
#
# Deciding on the closure costs passes (124 rather than 101) and is the difference between a
# corpus and a corpus-shaped hole.
claims = {}
for k in players:
    own = set()
    for n in closure(k):
        for t in loads.get(n, ()):
            own.add(("load", t))
        for t in repaints.get(n, ()):
            own.add(("editimage", t))
    claims[k] = own

adj = defaultdict(set)
owner = defaultdict(list)
for k in players:
    for t in claims[k]:
        owner[t].append(k)
for t, ms in owner.items():
    for i in range(len(ms)):
        for j in range(i + 1, len(ms)):
            adj[ms[i]].add(ms[j]); adj[ms[j]].add(ms[i])
worst = max((len(v) for v in owner.values()), default=0)
hottest = max(owner.items(), key=lambda kv: len(kv[1]))[0] if owner else ("-", "-")
print(f"clashes: worst pile-up {worst} closures on {hottest[0]} {hottest[1]}")

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

# Then the same colouring again, LEAST-LOADED first instead of lowest-first.
#
# A mod that clashes with nothing may go in any pass, and there are hundreds of them; the
# lowest-free rule sends every one to colour 0. That put 509 mods and 348 map packs in
# MapPass-01 while passes 80-87 held the framework list and almost nothing else - the longest
# game load, the most for Content Patcher to argue over, and 348 packs' maps all attributed to
# one profile name, which is the single thing this exercise exists to avoid.
#
# The first pass proved an assignment in n_profiles colours exists, and this walks the same
# order, so a free colour is always available and the count cannot grow.
load = defaultdict(int)
colour = {}
for k in order:
    used = {colour[n] for n in adj[k] if n in colour}
    c = min((x for x in range(n_profiles) if x not in used), key=lambda x: (load[x], x))
    colour[k] = c
    load[c] += 1
spread = sorted(load.values())
print(f"map mods per pass: smallest {spread[0]}, largest {spread[-1]}, "
      f"median {spread[len(spread) // 2]}")
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
           "note": (f"Coverage pass {c+1} of {n_profiles}. Two mods clash when they Load the "
                    f"same map (one wins, the other's map is never dumped) or when they EditImage "
                    f"the same map sheet (both apply, and the art dumped is a blend belonging to "
                    f"neither). One pass per claimant of the most contested target of either "
                    f"kind. Dump each; the dump accumulates on its own."),
           "enabled": dict(enabled)}
    json.dump(doc, open(os.path.join(GAME, "mod-profiles", f"{name}.json"), "w",
                        encoding="utf-8"), ensure_ascii=False, indent=2)
    written.append((name, sum(len(v) for v in enabled.values()),
                    len(enabled.get("04_Maps-World", [])) + len(enabled.get("11_Maps", []))
                    + len(enabled.get("10_Locations", []))))

for nm, tot, maps in written:
    print(f"  {nm}: {tot} mods ({maps} map packs)")
json.dump([w[0] for w in written], open(os.path.join(HERE, "mappasses.json"), "w"), indent=1)
