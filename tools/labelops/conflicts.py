"""Which map mods can share one profile, and which have to be dumped separately.

Two mods CONFLICT when both `Load` the same Maps/ target: Content Patcher lets exactly one
win, so the loser's map never reaches the dump. `EditMap`/`EditImage` patches stack, so they
never force a split. Read from each mod's content.json rather than guessed from the mod list.
"""
import json, os, re, sys
from collections import defaultdict
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"


def strip_jsonc(raw):
    out, i, n, in_str, esc = [], 0, len(raw), False, False
    while i < n:
        c = raw[i]
        if in_str:
            out.append(c)
            if esc: esc = False
            elif c == "\\": esc = True
            elif c == '"': in_str = False
            i += 1; continue
        if c == '"':
            in_str = True; out.append(c); i += 1; continue
        if c == "/" and i + 1 < n and raw[i+1] == "/":
            while i < n and raw[i] != "\n": i += 1
            continue
        if c == "/" and i + 1 < n and raw[i+1] == "*":
            j = raw.find("*/", i + 2); i = n if j < 0 else j + 2; continue
        out.append(c); i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def read_json(p):
    try:
        raw = open(p, encoding="utf-8-sig").read()
    except Exception:
        return None
    try:
        return json.loads(raw)
    except Exception:
        pass
    try:
        return json.loads(strip_jsonc(raw))
    except Exception:
        return None


def targets(mod_dir):
    """Map targets this mod LOADS (exclusive) and EDITS (stackable)."""
    loads, edits = set(), set()
    for dp, dns, fns in os.walk(mod_dir):
        for fn in fns:
            if fn.lower() != "content.json":
                continue
            d = read_json(os.path.join(dp, fn))
            if not isinstance(d, dict):
                continue
            for ch in (d.get("Changes") or []):
                if not isinstance(ch, dict):
                    continue
                act = str(ch.get("Action", "")).lower()
                tg = ch.get("Target")
                if not isinstance(tg, str):
                    continue
                for t in [x.strip() for x in tg.split(",")]:
                    if not t.lower().startswith("maps/"):
                        continue
                    if "{{" in t:              # a token target can hit anything; treat as edit
                        edits.add(t); continue
                    (loads if act == "load" else edits).add(t)
    return loads, edits


mods = {}
for root in ["Mods", "Mods (disabled)"]:
    base = os.path.join(GAME, root)
    if not os.path.isdir(base):
        continue
    for cat in sorted(os.listdir(base)):
        cp = os.path.join(base, cat)
        if not os.path.isdir(cp) or cat in ("06_Localization", "07_NSFW"):
            continue
        for mod in sorted(os.listdir(cp)):
            mp = os.path.join(cp, mod)
            if not os.path.isdir(mp):
                continue
            lo, ed = targets(mp)
            if lo or ed:
                mods[(cat, mod)] = (lo, ed, root == "Mods")

print(f"{len(mods)} mods touch Maps/ targets\n")

owner = defaultdict(list)
for k, (lo, _, _) in mods.items():
    for t in lo:
        owner[t].append(k)

clashes = {t: ms for t, ms in owner.items() if len(ms) > 1}
print(f"map targets LOADED by more than one mod: {len(clashes)}")
pairs = defaultdict(set)
for t, ms in clashes.items():
    for i in range(len(ms)):
        for j in range(i+1, len(ms)):
            a, b = sorted([ms[i], ms[j]])
            pairs[(a, b)].add(t)

print(f"conflicting mod PAIRS: {len(pairs)}\n")
for (a, b), ts in sorted(pairs.items(), key=lambda kv: -len(kv[1]))[:25]:
    print(f"  {a[1][:34]:36s} X {b[1][:34]:36s}  {len(ts)} maps")
    print(f"      e.g. {', '.join(sorted(ts)[:3])}")

# group into connected components = the profiles that must be separate
adj = defaultdict(set)
for a, b in pairs:
    adj[a].add(b); adj[b].add(a)
seen, comps = set(), []
for k in mods:
    if k in seen or k not in adj:
        continue
    stack, comp = [k], []
    while stack:
        c = stack.pop()
        if c in seen: continue
        seen.add(c); comp.append(c)
        stack.extend(adj[c] - seen)
    comps.append(comp)
print(f"\nconflict clusters: {len(comps)}")
for comp in sorted(comps, key=len, reverse=True):
    print(f"  cluster of {len(comp)}: " + ", ".join(sorted(m for _, m in comp))[:200])
free = [k for k in mods if k not in adj]
print(f"\nmods with NO load conflict (can go in every profile): {len(free)}")
json.dump({"pairs": [[list(a), list(b), sorted(ts)] for (a, b), ts in pairs.items()],
           "clusters": [[list(x) for x in c] for c in comps],
           "free": [list(x) for x in free]},
          open(os.path.join(os.path.dirname(__file__), "conflicts.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=1)
