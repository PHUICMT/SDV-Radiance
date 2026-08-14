"""What is enabled right now, and which of its required dependencies are not.

Reads EVERY manifest under a mod folder, not just the outermost one: big packs ship their CP /
SMAPI / FTM components several levels down and each declares its own dependencies. Missing one
does not stop the game booting - the pack just refuses to install and says so in a dialog.

    python checkdeps.py [ProfileName]      (default: whatever is enabled on disk)
"""
import json, os, re, sys
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
ON, OFF = os.path.join(GAME, "Mods"), os.path.join(GAME, "Mods (disabled)")


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
        if c == "/" and i+1 < n and raw[i+1] == "/":
            while i < n and raw[i] != "\n": i += 1
            continue
        if c == "/" and i+1 < n and raw[i+1] == "*":
            j = raw.find("*/", i+2); i = n if j < 0 else j+2; continue
        out.append(c); i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def read(p):
    try:
        raw = open(p, encoding="utf-8-sig").read()
    except Exception:
        return None
    for attempt in (raw, strip_jsonc(raw)):
        try:
            return json.loads(attempt)
        except Exception:
            pass
    return None


def scan(root):
    """(cat, folder) -> {'uids': [...], 'deps': [...]}"""
    out = {}
    if not os.path.isdir(root):
        return out
    for cat in sorted(os.listdir(root)):
        cp = os.path.join(root, cat)
        if not os.path.isdir(cp):
            continue
        for mod in sorted(os.listdir(cp)):
            mp = os.path.join(cp, mod)
            if not os.path.isdir(mp):
                continue
            uids, deps = [], []
            for dp, dns, fns in os.walk(mp):
                if "manifest.json" in fns:
                    m = read(os.path.join(dp, "manifest.json"))
                    if m:
                        if m.get("UniqueID"):
                            uids.append(m["UniqueID"])
                        for d in (m.get("Dependencies") or []):
                            if d.get("IsRequired") is not False and d.get("UniqueID"):
                                deps.append(d["UniqueID"])
                        cpf = m.get("ContentPackFor", {})
                        if isinstance(cpf, dict) and cpf.get("UniqueID"):
                            deps.append(cpf["UniqueID"])
                if dp.count(os.sep) - mp.count(os.sep) >= 4:
                    dns[:] = []
            if uids or deps:
                out[(cat, mod)] = {"uids": uids, "deps": deps}
    return out


enabled, parked = scan(ON), scan(OFF)
have = {u.lower() for v in enabled.values() for u in v["uids"]}
parked_by_id = {u.lower(): k for k, v in parked.items() for u in v["uids"]}

print(f"enabled: {len(enabled)} mod folders, {len(have)} unique IDs\n")
missing = {}
for k, v in enabled.items():
    own = {u.lower() for u in v["uids"]}
    for d in v["deps"]:
        dl = d.lower()
        if dl in have or dl in own:
            continue
        missing.setdefault(d, []).append(k[1])

if not missing:
    print("every required dependency of every enabled mod is present.")
else:
    print(f"MISSING DEPENDENCIES ({len(missing)}):")
    for d, users in sorted(missing.items()):
        where = parked_by_id.get(d.lower())
        fix = f"parked at {where[0]}/{where[1]} - just enable it" if where else "NOT on disk - needs downloading"
        print(f"  {d}")
        print(f"      needed by : {', '.join(sorted(set(users)))}")
        print(f"      fix       : {fix}")
