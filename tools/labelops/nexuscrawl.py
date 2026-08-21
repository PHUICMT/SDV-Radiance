"""Page the map-relevant Nexus categories deep, and say what is not here yet.

nexustop.py stops at the first 50 of each category and nexusfind.py at 200 of Buildings. The
labelling question is not "what are the famous ones", it is "which packs repaint a Maps/ sheet
we have painted labels on", and that tail is long: a pack with 4,000 downloads repaints the town
just as thoroughly as one with 900,000.

Read-only. Writes nexus-crawl.json for nexusget.py to work from.

    python nexuscrawl.py [--depth 600]
"""
import argparse, json, os, re, sys, time, urllib.request
sys.stdout.reconfigure(encoding="utf-8")

REPO = r"e:\Games\GamesMods\DevStardew\SDV-Radiance"
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HERE = os.path.dirname(os.path.abspath(__file__))
GQL = "https://api.nexusmods.com/v2/graphql"

# Where a repaint of an existing map can come from. "Buildings" is the category the user asked
# about; the other three reach the same tilesheets by another route, and a recolour is the most
# thorough repainter of all of them.
CATEGORIES = ["Buildings", "Locations", "Maps", "Visuals and Graphics"]

key = None
for line in open(os.path.join(REPO, ".env"), encoding="utf-8"):
    if "NEXUS" in line and "=" in line:
        key = line.split("=", 1)[1].strip().strip('"').strip("'")

QUERY = """
query M($filter: ModsFilter, $sort: [ModsSort!], $count: Int, $offset: Int) {
  mods(filter: $filter, sort: $sort, count: $count, offset: $offset) {
    nodes { modId name downloads adult modCategory { name } uploader { name } }
    totalCount
  }
}
"""


def graph(category, offset, count=50):
    body = json.dumps({"query": QUERY, "variables": {
        "filter": {"gameId": [{"value": "1303", "op": "EQUALS"}],
                   "categoryName": [{"value": category, "op": "EQUALS"}]},
        "sort": [{"downloads": {"direction": "DESC"}}],
        "count": count, "offset": offset}}).encode()
    request = urllib.request.Request(GQL, data=body, headers={
        "Content-Type": "application/json", "apikey": key,
        "User-Agent": "SDV-Radiance-labelplan/1.0"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return json.load(response)
        except Exception:
            if attempt == 3:
                raise
            time.sleep(3 + attempt * 4)


def installed_ids():
    """Every Nexus id already on disk, enabled or parked, read from manifest UpdateKeys."""
    found = set()
    for root in ("Mods", "Mods (disabled)"):
        base = os.path.join(GAME, root)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            # Walked in full: a manifest sits up to four folders below a mod's own folder
            # in this library, and a bundle legitimately carries several. A depth cap here
            # under-counts what is installed, which is how a re-download of something already
            # on disk gets planned.
            dirnames[:] = [d for d in dirnames if d not in (".git", "node_modules", "__pycache__")]
            if "manifest.json" not in filenames:
                continue
            try:
                raw = open(os.path.join(dirpath, "manifest.json"), encoding="utf-8-sig").read()
            except OSError:
                continue
            for match in re.finditer(r'"Nexus:\s*(\d+)"', raw):
                found.add(int(match.group(1)))
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--depth", type=int, default=600, help="how deep to page each category")
    args = parser.parse_args()

    have = installed_ids()
    print(f"already on disk: {len(have)} Nexus ids\n")

    rows, seen = [], set()
    for category in CATEGORIES:
        offset, kept = 0, 0
        total = None
        while offset < args.depth:
            page = graph(category, offset)
            if "errors" in page:
                print(f"{category} @{offset}: {json.dumps(page['errors'])[:200]}")
                break
            block = page["data"]["mods"]
            total = total if total is not None else block["totalCount"]
            nodes = block["nodes"]
            if not nodes:
                break
            for node in nodes:
                if node["modId"] in seen:
                    continue
                seen.add(node["modId"])
                rows.append({"cat": category, "id": node["modId"], "name": node["name"],
                             "dl": node["downloads"], "adult": node["adult"],
                             "by": (node.get("uploader") or {}).get("name", ""),
                             "have": node["modId"] in have})
                kept += 1
            offset += len(nodes)
            time.sleep(0.4)
        print(f"{category:<22} {kept:>4} listed of {total:,} in the category")

    missing = [r for r in rows if not r["have"] and not r["adult"]]
    out = os.path.join(HERE, "nexus-crawl.json")
    json.dump(rows, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"\n{len(rows)} listed, {len(missing)} not here yet -> {os.path.basename(out)}")
    for category in CATEGORIES:
        n = sum(1 for r in missing if r["cat"] == category)
        print(f"  missing in {category:<22} {n}")


if __name__ == "__main__":
    main()
