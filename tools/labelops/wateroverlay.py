"""Lay the water tool's changes over the shipped labels, liquid only.

The water labels tool (tools/labelers/water) keeps what it changed in one file per sheet under
<store>/water/. This puts those tiles into labels/water-labels.json with one rule: a pixel the
tool or the shipped label calls liquid (water, ice, flowing, lava, hot water) takes the tool's
value; every other pixel keeps the shipped one. So the water tool owns the liquid and HF Studio
keeps owning the walls, roofs and everything else, and a later HF Studio sync that repaints a wall
in the same tile is never undone by an old water edit.

    python tools/labelops/wateroverlay.py                 apply, report what changed
    python tools/labelops/wateroverlay.py --dry-run       report only
    python tools/labelops/wateroverlay.py --store PATH --db PATH

tools/sync-labels.ps1 runs it after the HF Studio merge, so every sync carries both.
"""
import argparse, base64, json, os, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB = os.path.join(REPO, "labels", "water-labels.json")
STORE = os.path.join(os.path.dirname(REPO), "_label-store")
LIQUID = {1, 9, 10, 11, 14}


def lay_over(shipped, changed):
    """The tile with the tool's liquid laid over the shipped one."""
    out = bytearray(shipped)
    for i in range(256):
        if changed[i] in LIQUID or shipped[i] in LIQUID:
            out[i] = changed[i]
    return bytes(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--store", default=STORE)
    parser.add_argument("--db", default=DB)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    folder = os.path.join(args.store, "water")
    if not os.path.isdir(folder):
        print(f"no water changes yet ({folder} does not exist); nothing to lay over")
        return
    with open(args.db, encoding="utf-8") as file:
        db = json.load(file)
    sheets = db["sheets"]
    changed_tiles = changed_sheets = new_sheets = 0
    for entry in sorted(os.listdir(folder)):
        if not entry.endswith(".json"):
            continue
        with open(os.path.join(folder, entry), encoding="utf-8") as file:
            record = json.load(file)
        name = record["sheet"]
        sheet = sheets.get(name)
        if sheet is None:
            # a sheet nobody labelled before: it starts as all ground under the tool's liquid
            sheet = sheets[name] = {"size": record.get("size"), "tiles": {}}
            new_sheets += 1
        touched = 0
        for tile, value in record["tiles"].items():
            mine = base64.b64decode(value["classes"])
            shipped = base64.b64decode(sheet["tiles"][tile]) if tile in sheet["tiles"] else bytes(256)
            merged = lay_over(shipped, mine)
            if merged != shipped:
                sheet["tiles"][tile] = base64.b64encode(merged).decode()
                touched += 1
        if touched:
            changed_tiles += touched
            changed_sheets += 1
            print(f"  {name}: {touched} tile(s)")
    print(f"water overlay: {changed_tiles} tile(s) on {changed_sheets} sheet(s) differ from labels/water-labels.json"
          + (f", {new_sheets} sheet(s) new to it" if new_sheets else ""))
    if args.dry_run or not changed_tiles:
        return
    with open(args.db + ".tmp", "w", encoding="utf-8") as file:
        json.dump(db, file, separators=(",", ":"))  # as mergelabels.py writes it
    os.replace(args.db + ".tmp", args.db)
    print(f"written {args.db}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
