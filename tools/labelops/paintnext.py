"""Which sheets are worth painting next, ranked by how much of the corpus they cover.

The dump holds far more sheets than are worth a person's afternoon. A sheet that one interior
places once is not the same job as one that three hundred map versions place, and the only way
to tell them apart is to count. So count: for every sheet the corpus places, how many location
versions use it, how many distinct places that is, and whether it already has labels.

    python tools/labelops/paintnext.py              the top 40 unpainted, by reach
    python tools/labelops/paintnext.py --all        every sheet, painted or not
    python tools/labelops/paintnext.py --painted    what is already done, same ranking
    python tools/labelops/paintnext.py -n 100       a different cut-off
"""
import argparse, io, json, os, sys
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
INDEX = os.path.join(HFDIR, "maps.json")

parser = argparse.ArgumentParser()
parser.add_argument("--all", action="store_true", help="painted and unpainted together")
parser.add_argument("--painted", action="store_true", help="only sheets that already have labels")
parser.add_argument("-n", type=int, default=40, help="how many rows to print")
args = parser.parse_args()


def load(path):
    with io.open(path, encoding="utf-8") as handle:
        return json.load(handle)


document = load(INDEX)
locations = document["locations"]

# A sheet is painted if a labels file exists for it. Both layouts the studio has used are read:
# one file per sheet beside maps.json, and a labels/ folder.
painted = set()
for name in os.listdir(HFDIR):
    if name.endswith(".labels.json"):
        painted.add(name[: -len(".labels.json")])
labels_dir = os.path.join(HFDIR, "labels")
if os.path.isdir(labels_dir):
    for name in os.listdir(labels_dir):
        if name.endswith(".json"):
            painted.add(name[:-5])

versions_using = defaultdict(int)      # sheet -> how many location VERSIONS place it
places_using = defaultdict(set)        # sheet -> the distinct places, whatever the version
outdoor_versions = defaultdict(int)    # sheet -> versions that are outdoors, where water lives

for entry in locations.values():
    sheets = entry.get("sheets") or []
    place = entry.get("name", "")
    outdoors = bool(entry.get("outdoors"))
    for sheet in dict.fromkeys(sheets):     # a sheet listed twice is still one use
        versions_using[sheet] += 1
        places_using[sheet].add(place)
        if outdoors:
            outdoor_versions[sheet] += 1

rows = []
for sheet, versions in versions_using.items():
    done = sheet in painted
    if args.painted and not done:
        continue
    if not args.all and not args.painted and done:
        continue
    rows.append((versions, len(places_using[sheet]), outdoor_versions[sheet], done, sheet))
rows.sort(reverse=True)

total_versions = len(locations)
state = "already painted" if args.painted else ("every sheet" if args.all else "not yet painted")
print(f"{len(versions_using):,} sheets are placed by {total_versions:,} location version(s); "
      f"{len(painted):,} have labels\n")
print(f"top {min(args.n, len(rows))} by reach, {state}:\n")
print(f"{'versions':>9}{'places':>8}{'outdoor':>9}  {'':3} sheet")
for versions, places, outdoor, done, sheet in rows[:args.n]:
    print(f"{versions:9,}{places:8,}{outdoor:9,}  {'[x]' if done else '[ ]'} {sheet}")

covered = sum(r[0] for r in rows[:args.n])
print(f"\nthose {min(args.n, len(rows))} sheets account for {covered:,} sheet-uses")
if not args.all and not args.painted:
    done_uses = sum(v for s, v in versions_using.items() if s in painted)
    all_uses = sum(versions_using.values())
    print(f"painting is {100.0 * done_uses / max(1, all_uses):.1f}% of all sheet-uses so far "
          f"({done_uses:,} of {all_uses:,})")
