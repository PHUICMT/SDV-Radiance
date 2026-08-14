"""Fold an HF Studio export into the shipped label DB without dropping painted work.

The sync used to be a file copy, and a copy is only correct while the export is a superset of
what ships. It stopped being one. Measured on the 2026-08-14 export against the 2026-08-04 DB,
a straight copy would have silently thrown away:

  - 40 tiles on Pathoschild.CentralStation_Tiles, the whole sheet's glass. The export carries
    the same sheet under a LEADING DOT and empty, so the name that LabelStore actually looks up
    simply vanished and nothing in the old script could notice.
  - 9 winter veto tiles (winter_outdoorsTileSheet, winter_town). Those all-zero tiles are the
    snow veto, the thing that fixed Four Corners in winter, and they are invisible to any check
    that counts painted pixels because they have none.

Meanwhile the export genuinely carried 2,095 new liquid tiles and 25 corrections, so refusing
it was not an answer either. Hence a merge with an explicit conflict rule:

  1. The export is the base. It has the newest paint, so it wins any tile that exists in both,
     which is what makes a repaint (volcano_dungeon water -> flowing) actually land.
  2. Sheets with no tiles carry no information, only a size. The 2026-08-14 export had 1,650 of
     them, left behind by the map dump passes. Dropped.
  3. Anything in the DB that step 1 did not cover is added back.
  4. If a single DB tile would still be missing after that, nothing is written at all.

    python tools/labelops/mergelabels.py                       newest export -> labels/water-labels.json
    python tools/labelops/mergelabels.py --dry-run             report, write nothing
    python tools/labelops/mergelabels.py --export X --db Y --out Z
"""
import argparse, base64, glob, json, os, sys, time
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DB = os.path.join(REPO, "labels", "water-labels.json")
STUDIO = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio")

# Index in the file's own "classes" array. Named here only to make the report readable.
CLASS = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
         "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]


def newest_export():
    """The labeler's AUTO save lands in HF-Studio\\labels\\, a manual Export all in the root.
    Searching only the root meant every sync used the last manual export and threw away hours
    of painting the auto-save had already written, so both are searched and newest wins."""
    found = glob.glob(os.path.join(STUDIO, "radiance-labels*.json"))
    found += glob.glob(os.path.join(STUDIO, "labels", "radiance-labels*.json"))
    if not found:
        raise SystemExit(f"no radiance-labels*.json under {STUDIO} or {STUDIO}\\labels")
    return max(found, key=os.path.getmtime)


def load(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def stats(sheets):
    tiles = veto = liquid = 0
    cls = Counter()
    for sh in sheets.values():
        for b64 in sh.get("tiles", {}).values():
            tiles += 1
            nz = Counter(b for b in base64.b64decode(b64) if b)
            if nz:
                liquid += 1
                cls.update(nz)
            else:
                veto += 1
    return tiles, liquid, veto, cls


def name(c):
    return CLASS[c] if 0 <= c < len(CLASS) else f"class {c}"


def merge(export, db):
    out = {"format": export.get("format", db.get("format")),
           "classes": export.get("classes", db.get("classes")),
           "sheets": {}}
    dropped_empty = dropped_unnamed = 0
    for sheet, body in export["sheets"].items():
        tiles = body.get("tiles", {})
        if not sheet.strip():
            dropped_unnamed += 1
            continue
        if not tiles:
            dropped_empty += 1
            continue
        out["sheets"][sheet] = {"size": body.get("size"), "tiles": dict(tiles)}

    back_sheets = back_tiles = 0
    restored = []
    for sheet, body in db["sheets"].items():
        tiles = body.get("tiles", {})
        if not tiles:
            continue
        if sheet not in out["sheets"]:
            out["sheets"][sheet] = {"size": body.get("size"), "tiles": dict(tiles)}
            back_sheets += 1
            back_tiles += len(tiles)
            restored.append((sheet, len(tiles)))
            continue
        dst = out["sheets"][sheet]["tiles"]
        n = 0
        for k, v in tiles.items():
            if k not in dst:
                dst[k] = v
                n += 1
        if n:
            back_tiles += n
            restored.append((sheet, n))
    return out, dropped_empty, dropped_unnamed, back_sheets, back_tiles, restored


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--export", help="HF Studio export (default: the newest one)")
    ap.add_argument("--db", default=DB, help="the DB the mod ships")
    ap.add_argument("--out", help="where to write (default: over --db)")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    src = a.export or newest_export()
    out_path = a.out or a.db
    print(f"export  {src}")
    print(f"        {os.path.getsize(src)/1048576:.2f} MB, written "
          f"{time.strftime('%Y-%m-%d %H:%M', time.localtime(os.path.getmtime(src)))}")
    print(f"db      {a.db}")

    export, db = load(src), load(a.db)
    if "sheets" not in export:
        raise SystemExit("no 'sheets' object: that is not an export-all file.")

    merged, empty, unnamed, back_sheets, back_tiles, restored = merge(export, db)

    # The whole point of the exercise: nothing the mod already shipped may go missing.
    lost = [(s, k) for s, body in db["sheets"].items() for k in body.get("tiles", {})
            if k not in merged["sheets"].get(s, {}).get("tiles", {})]
    if lost:
        for s, k in lost[:20]:
            print(f"  LOST {s} tile {k}")
        raise SystemExit(f"refusing to write: {len(lost)} tiles from the DB are missing.")

    dt, dl, dv, dc = stats(db["sheets"])
    mt, ml, mv, mc = stats(merged["sheets"])
    if mt == 0:
        raise SystemExit("merged result has 0 tiles: refusing to write.")

    print(f"\ndropped {empty} sheets carrying no tiles"
          + (f", {unnamed} unnamed" if unnamed else ""))
    print(f"kept back from the DB: {back_sheets} whole sheets, {back_tiles} tiles")
    for s, n in sorted(restored, key=lambda r: -r[1])[:8]:
        print(f"    {s:<44} {n:>4}")

    print(f"\n{'':<10}{'sheets':>9}{'tiles':>9}{'liquid':>9}{'veto':>9}")
    print(f"{'db':<10}{len(db['sheets']):>9}{dt:>9}{dl:>9}{dv:>9}")
    print(f"{'merged':<10}{len(merged['sheets']):>9}{mt:>9}{ml:>9}{mv:>9}")
    print(f"{'delta':<10}{len(merged['sheets'])-len(db['sheets']):>+9}"
          f"{mt-dt:>+9}{ml-dl:>+9}{mv-dv:>+9}")

    moved = [(c, mc[c] - dc[c]) for c in sorted(set(mc) | set(dc)) if mc[c] != dc[c]]
    if moved:
        print("\npixels by class:")
        for c, d in moved:
            print(f"    {name(c):<14}{dc[c]:>10,} -> {mc[c]:>10,}  ({d:+,})")

    if a.dry_run:
        print("\ndry run: nothing written.")
        return
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(merged, f, separators=(",", ":"))
    print(f"\nwrote {out_path}  ({os.path.getsize(out_path)/1048576:.2f} MB)")


if __name__ == "__main__":
    main()
