"""Page every map/visual category by downloads and write one candidate list.

Read-only against the API and the Mods folders, so it is safe to run while a download pass is
still unpacking. Adult mods are left out; so is anything already installed.

    python nexuslist.py [per-category-count]
"""
import json, os, re, sys, time, urllib.request
sys.stdout.reconfigure(encoding="utf-8")

REPO = r"e:\Games\GamesMods\DevStardew\SDV-Radiance"
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HERE = os.path.dirname(os.path.abspath(__file__))
WANT = int(sys.argv[1]) if len(sys.argv) > 1 else 200
CATS = ["Buildings", "Locations", "Maps", "Visuals and Graphics"]

key = None
for line in open(os.path.join(REPO, ".env"), encoding="utf-8"):
    if "NEXUS" in line and "=" in line:
        key = line.split("=", 1)[1].strip().strip('"').strip("'")

Q = """
query M($f: ModsFilter, $s: [ModsSort!], $c: Int, $o: Int) {
  mods(filter: $f, sort: $s, count: $c, offset: $o) {
    nodes { modId name downloads adult modCategory { name } }
    totalCount
  }
}
"""


def page(cat, offset, count=50):
    body = json.dumps({"query": Q, "variables": {
        "f": {"gameId": [{"value": "1303", "op": "EQUALS"}],
              "categoryName": [{"value": cat, "op": "EQUALS"}]},
        "s": [{"downloads": {"direction": "DESC"}}],
        "c": count, "o": offset}}).encode()
    req = urllib.request.Request("https://api.nexusmods.com/v2/graphql", data=body, headers={
        "Content-Type": "application/json", "apikey": key,
        "User-Agent": "SDV-Radiance-labelplan/1.0"})
    for a in range(4):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except Exception:
            if a == 3:
                raise
            time.sleep(3 + a * 4)


installed = set()
for root in ["Mods", "Mods (disabled)"]:
    base = os.path.join(GAME, root)
    if not os.path.isdir(base):
        continue
    for dp, dns, fns in os.walk(base):
        if dp.count(os.sep) - base.count(os.sep) > 4:
            dns[:] = []
        for fn in fns:
            if fn != "manifest.json":
                continue
            try:
                raw = open(os.path.join(dp, fn), encoding="utf-8-sig").read()
            except Exception:
                continue
            for m in re.finditer(r'"Nexus:\s*(\d+)"', raw):
                installed.add(int(m.group(1)))
print(f"already installed: {len(installed)} Nexus ids\n")

rows, seen = [], set()
for cat in CATS:
    got = 0
    for off in range(0, WANT, 50):
        d = page(cat, off)
        nodes = (d.get("data", {}).get("mods", {}) or {}).get("nodes") or []
        if not nodes:
            break
        for n in nodes:
            if n["adult"] or n["modId"] in seen:
                continue
            seen.add(n["modId"])
            rows.append({"cat": cat, "id": n["modId"], "name": n["name"],
                         "dl": n["downloads"], "have": n["modId"] in installed})
            got += 1
        time.sleep(0.4)
    have = sum(1 for r in rows if r["cat"] == cat and r["have"])
    print(f"  {cat:22s} listed {got:4d}   already installed {have}")

out = os.path.join(HERE, "nexus-all.json")
json.dump(rows, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
todo = [r for r in rows if not r["have"]]
print(f"\n{len(rows)} listed, {len(todo)} not installed -> {out}")
