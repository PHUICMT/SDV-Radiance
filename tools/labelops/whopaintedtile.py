"""Per TILE, which installed packs claim a patch covering it.

MapArtClaims in the mod answers this per SHEET, which is enough to name suspects in a report and
not enough to plan work: spring_town is claimed by four packs, and knowing that says nothing about
which of its 2,304 tiles each one actually touches.

A Content Patcher EditImage carries a ToArea, a rectangle in sheet pixels, so the tiles a patch
lands on are arithmetic rather than a guess. This reads every installed pack's patch list, turns
each rectangle into tile indices, and answers per tile.

    python whopaintedtile.py spring_town              every claimed tile, grouped by pack
    python whopaintedtile.py spring_town --glass       only tiles this mod has painted glass on
    python whopaintedtile.py spring_town --tile 412    who claims one tile
    python whopaintedtile.py --list                    sheets any pack claims, most claimed first

Honest about its limits, and they matter:
  * a patch switched off in a pack's config still counts as a claim
  * a patch with conditions counts even where the conditions never hold
  * where two packs claim one tile, only one of them won, and which is load order
  * a patch with no ToArea replaces the WHOLE sheet, and is reported as such
  * a mod that edits art from C# rather than from a patch list is invisible here
"""
import argparse, base64, collections, json, os, re, sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
LABELS = os.path.join(REPO, "labels", "water-labels.json")
SEASONS = ("spring", "summer", "fall", "winter")
TILE = 16
MAXIMUM_PATCH_BYTES = 8 * 1024 * 1024


def tolerant_json(text):
    """Content Patcher files are JSON with comments and trailing commas. Both are stripped.

    Walked one character at a time rather than pattern matched, because a comment can begin at the
    end of a line that has content on it, and a string can contain the two characters that start
    one. A line-anchored regex misses the first; a greedy one eats every https:// in the file. On
    this library the regex version left 119 patch files unread, and an unread file reads exactly
    like a mod that claims nothing.
    """
    out = []
    i, length = 0, len(text)
    in_string = escaped = False
    while i < length:
        char = text[i]
        if in_string:
            out.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            i += 1
            continue
        if char == '"':
            in_string = True
            out.append(char)
            i += 1
            continue
        if char == "/" and i + 1 < length and text[i + 1] == "/":
            while i < length and text[i] != "\n":
                i += 1
            continue
        if char == "/" and i + 1 < length and text[i + 1] == "*":
            closing = text.find("*/", i + 2)
            i = length if closing < 0 else closing + 2
            continue
        out.append(char)
        i += 1
    stripped = re.sub(r",(\s*[}\]])", r"\1", "".join(out))
    return json.loads(stripped)


def normalize(name):
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    return name[:-4] if name.lower().endswith(".png") else name


def sheet_names(target):
    """Every sheet name one Target string can mean, seasons expanded."""
    out = []
    for one in target.split(","):
        one = one.strip()
        if not one.lower().startswith(("maps/", "maps\\")):
            continue
        name = normalize(one)
        if "{{" in name:
            head, _, rest = name.partition("{{")
            token, _, tail = rest.partition("}}")
            if token.strip().lower() == "season":
                out.extend(head + s + tail for s in SEASONS)
            continue           # any other token cannot be resolved to a name from here
        out.append(name)
    return out


def pack_of(path, root):
    at = os.path.dirname(os.path.abspath(path))
    root = os.path.abspath(root)
    while at and len(at) > len(root):
        if os.path.exists(os.path.join(at, "manifest.json")):
            return os.path.basename(at)
        at = os.path.dirname(at)
    return os.path.basename(os.path.dirname(path))


def scan(roots):
    """{sheet: {tileKey: {pack, ...}}} plus {sheet: {pack, ...}} for whole-sheet patches."""
    per_tile = collections.defaultdict(lambda: collections.defaultdict(set))
    whole = collections.defaultdict(set)
    read = failed = 0
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            for filename in filenames:
                if not filename.endswith(".json") or filename in ("manifest.json", "config.json"):
                    continue
                path = os.path.join(dirpath, filename)
                try:
                    if os.path.getsize(path) > MAXIMUM_PATCH_BYTES:
                        continue
                    text = open(path, encoding="utf-8-sig", errors="replace").read()
                except OSError:
                    continue
                if '"Target"' not in text or '"Changes"' not in text:
                    continue
                try:
                    document = tolerant_json(text)
                    read += 1
                except Exception:
                    failed += 1
                    continue
                pack = pack_of(path, root)
                for patch in document.get("Changes") or []:
                    if not isinstance(patch, dict):
                        continue
                    for sheet in sheet_names(str(patch.get("Target") or "")):
                        area = patch.get("ToArea")
                        if not isinstance(area, dict):
                            whole[sheet].add(pack)
                            continue
                        try:
                            x, y = int(area["X"]), int(area["Y"])
                            w, h = int(area["Width"]), int(area["Height"])
                        except (KeyError, TypeError, ValueError):
                            whole[sheet].add(pack)
                            continue
                        for ty in range(y // TILE, (y + h + TILE - 1) // TILE):
                            for tx in range(x // TILE, (x + w + TILE - 1) // TILE):
                                per_tile[sheet][(tx, ty)].add(pack)
    return per_tile, whole, read, failed


def glass_tiles_of(sheet):
    with open(LABELS, encoding="utf-8") as f:
        labels = json.load(f)
    classes = labels["classes"]
    wanted = {classes.index(n) for n in ("mirror", "window", "glass")}
    body = labels["sheets"].get(sheet) or {}
    size = body.get("size")
    width_tiles = None
    if isinstance(size, str):
        try:
            width_tiles = json.loads(size)[0] // TILE
        except Exception:
            width_tiles = None
    elif isinstance(size, list) and size:
        width_tiles = size[0] // TILE
    out = set()
    for index, painted in (body.get("tiles") or {}).items():
        if set(base64.b64decode(painted)) & wanted:
            out.add(int(index))
    return out, width_tiles


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("sheet", nargs="?")
    parser.add_argument("--glass", action="store_true", help="only tiles with a glass label")
    parser.add_argument("--tile", type=int, help="one tile index")
    parser.add_argument("--list", action="store_true", help="sheets any pack claims")
    parser.add_argument("--parked", action="store_true", help="include Mods (disabled)")
    args = parser.parse_args()

    roots = [os.path.join(GAME, "Mods")]
    if args.parked:
        roots.append(os.path.join(GAME, "Mods (disabled)"))
    per_tile, whole, read, failed = scan(roots)
    print(f"read {read} patch file(s), {failed} unreadable")

    if args.list or not args.sheet:
        rows = sorted(((len(t), s) for s, t in per_tile.items()), reverse=True)
        print(f"\n{'tiles claimed':>14}  sheet")
        for count, sheet in rows[:30]:
            print(f"{count:>14}  {sheet}")
        return

    sheet = args.sheet
    glass, width_tiles = glass_tiles_of(sheet)
    claimed = per_tile.get(sheet, {})
    if whole.get(sheet):
        print(f"\nwhole-sheet patches on {sheet}: {', '.join(sorted(whole[sheet]))}")
        print("  those replace or edit the sheet without saying where, so every tile below may")
        print("  also be theirs.")
    if not claimed:
        print(f"\nno pack claims a rectangle on {sheet}")
        return

    if width_tiles is None:
        print(f"\n{sheet}: the label pack does not record a size, so tile indices cannot be worked"
              f" out from the rectangles. Reporting by tile coordinate instead.")
    if args.tile is not None and width_tiles:
        coordinate = (args.tile % width_tiles, args.tile // width_tiles)
        packs = claimed.get(coordinate)
        print(f"\ntile {args.tile} at {coordinate}: "
              + (", ".join(sorted(packs)) if packs else "claimed by nobody"))
        return

    by_pack = collections.Counter()
    glass_by_pack = collections.Counter()
    for coordinate, packs in claimed.items():
        index = coordinate[1] * width_tiles + coordinate[0] if width_tiles else None
        is_glass = index is not None and index in glass
        if args.glass and not is_glass:
            continue
        for pack in packs:
            by_pack[pack] += 1
            if is_glass:
                glass_by_pack[pack] += 1
    what = "glass tiles" if args.glass else "tiles"
    print(f"\n{sheet}: who claims which {what}")
    print(f"{'tiles':>7}{'of which glass':>16}  pack")
    for pack, count in by_pack.most_common():
        print(f"{count:>7}{glass_by_pack[pack]:>16}  {pack}")
    contested = sum(1 for c, p in claimed.items() if len(p) > 1)
    print(f"\n{len(claimed)} tile(s) claimed in total, {contested} of them by more than one pack;"
          f" for those, load order decided and this cannot say which won.")


if __name__ == "__main__":
    main()
