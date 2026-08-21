"""Rank installed mods by how much of this mod's painted work their art replaces.

A mod that repaints a tilesheet under the same name inherits the labels painted for the picture
that used to be there, and the reflection then lands where the base game's window used to be.
Which mods do that is answerable without launching anything: a Content Patcher pack says which
assets it targets, in its own content.json.

What matters is not how many sheets a pack repaints but how many LABELLED tiles it lands on, and
how many of those carry glass, because glass is what the guard actually withholds. A pack that
repaints twenty sheets nobody has painted costs nothing and should not be near the top of a
worklist.

    python mapartmods.py                 rank every installed pack
    python mapartmods.py --glass-only    rank by glass tiles alone
    python mapartmods.py --parked        include Mods (disabled) as well

Counts are what a pack CLAIMS. A patch switched off in its config still counts, a mod that edits
art from C# is invisible here, and where two packs claim one sheet only one of them won.
"""
import argparse, base64, collections, json, os, re, sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
LABELS = os.path.join(REPO, "labels", "water-labels.json")
TARGET = re.compile(r'"Target"\s*:\s*"([^"]+)"')
SEASONS = ("spring", "summer", "fall", "winter")
MAXIMUM_PATCH_BYTES = 8 * 1024 * 1024


def normalize(name):
    name = name.replace("\\", "/")
    name = name.rsplit("/", 1)[-1]
    return name[:-4] if name.lower().endswith(".png") else name


def labelled_tiles():
    """{sheet: (labelledCount, glassCount)} from the shipped paint."""
    with open(LABELS, encoding="utf-8") as f:
        labels = json.load(f)
    classes = labels["classes"]
    glass = {classes.index(n) for n in ("mirror", "window", "glass")}
    out = {}
    for sheet, body in labels["sheets"].items():
        tiles = body.get("tiles") or {}
        painted = sum(1 for v in tiles.values() if set(base64.b64decode(v)) & glass)
        out[normalize(sheet)] = (len(tiles), painted)
    return out


def pack_of(path, root):
    at = os.path.dirname(os.path.abspath(path))
    root = os.path.abspath(root)
    while at and len(at) > len(root):
        if os.path.exists(os.path.join(at, "manifest.json")):
            return os.path.basename(at)
        at = os.path.dirname(at)
    return os.path.basename(os.path.dirname(path))


def claims_in(root):
    """{pack: {sheet, ...}} for every Maps/ target declared under this folder."""
    found = collections.defaultdict(set)
    if not os.path.isdir(root):
        return found
    for dirpath, dirnames, filenames in os.walk(root):
        for filename in filenames:
            if not filename.endswith(".json") or filename in ("manifest.json", "config.json"):
                continue
            path = os.path.join(dirpath, filename)
            try:
                if os.path.getsize(path) > MAXIMUM_PATCH_BYTES:
                    continue
                with open(path, encoding="utf-8-sig", errors="replace") as f:
                    text = f.read()
            except OSError:
                continue
            if '"Target"' not in text:
                continue
            pack = pack_of(path, root)
            for match in TARGET.finditer(text):
                for one in match.group(1).split(","):
                    one = one.strip()
                    if not one.lower().startswith(("maps/", "maps\\")):
                        continue
                    sheet = normalize(one)
                    if "{{" in sheet:
                        # {{season}} is how nearly every pack writes its four seasonal copies.
                        head, _, rest = sheet.partition("{{")
                        token, _, tail = rest.partition("}}")
                        if token.strip().lower() == "season":
                            for season in SEASONS:
                                found[pack].add(head + season + tail)
                            continue
                        # An unknown token is matched by whatever plain text follows it, but only
                        # when there is enough of it to mean something. Targets like
                        # Maps/{{FarmMap}} leave nothing after the token, and an empty ending
                        # matches every sheet ever painted, which reads as "this pack repaints
                        # everything" for a pack that repaints one farm.
                        if len(tail) >= 4:
                            found[pack].add("~" + tail)
                    else:
                        found[pack].add(sheet)
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--glass-only", action="store_true")
    parser.add_argument("--parked", action="store_true", help="include Mods (disabled)")
    parser.add_argument("--top", type=int, default=30)
    args = parser.parse_args()

    painted = labelled_tiles()
    roots = [os.path.join(GAME, "Mods")]
    if args.parked:
        roots.append(os.path.join(GAME, "Mods (disabled)"))

    rows = []
    for root in roots:
        where = "installed" if root.endswith("Mods") else "parked"
        for pack, sheets in claims_in(root).items():
            hit_sheets, tiles, glass = [], 0, 0
            for sheet in sheets:
                if sheet.startswith("~"):
                    for known, (count, glass_count) in painted.items():
                        if known.lower().endswith(sheet[1:].lower()):
                            hit_sheets.append(known)
                            tiles += count
                            glass += glass_count
                elif sheet in painted:
                    hit_sheets.append(sheet)
                    tiles += painted[sheet][0]
                    glass += painted[sheet][1]
            rows.append((glass, tiles, len(sheets), len(set(hit_sheets)), pack, where))

    rows.sort(key=lambda r: (-r[0], -r[1]) if args.glass_only else (-r[1], -r[0]))
    print(f"{'glass':>7}{'labelled':>10}{'sheets hit':>12}{'claims':>8}  pack")
    for glass, tiles, claims, hit, pack, where in rows[:args.top]:
        if tiles == 0:
            continue
        print(f"{glass:>7}{tiles:>10}{hit:>12}{claims:>8}  {pack} ({where})")
    silent = sum(1 for r in rows if r[1] == 0)
    print(f"\n{len(rows)} pack(s) claim map art; {silent} of them land on no labelled tile at all,"
          f" so they cost nothing and are left out above.")


if __name__ == "__main__":
    main()
