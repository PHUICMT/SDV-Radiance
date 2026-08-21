"""Point a map dump's art index back at the PNG files that are actually on disk.

The dump writes one PNG per sheet named "<sheet>_<hash>.png", where the hash is FNV-1a over the
asset path it was loaded from. Older dumps wrote "<sheet>.png" with no hash, so an index from
before that change names files that no longer exist and every map opens with no art at all.

Nothing needs re-dumping to fix it: the index already records artSrc, the asset path each sheet
came from, and the hash is a pure function of that path. This recomputes the file name for every
entry and keeps the ones that are really there.

    python relinkart.py <maps.json> [--out maps.json] [--dry]

Prints how many entries were relinked, how many had no file, and how many PNGs on disk no index
entry claims, because a silent partial fix is what this whole exercise keeps being about.
"""
import argparse, json, os, shutil, sys

sys.stdout.reconfigure(encoding="utf-8")

HFDIR = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio")
INVALID = set('<>:"/\\|?*')


def art_file_name(sheet, source):
    """The dump's own naming, reproduced: sanitized sheet name, then FNV-1a over the path."""
    safe = "".join("_" if c in INVALID else c for c in sheet)
    hashed = 2166136261
    for char in source.lower():
        hashed = ((hashed ^ ord(char)) * 16777619) & 0xFFFFFFFF
    return f"{safe}_{hashed:08x}.png"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("maps", help="the maps.json to repair")
    parser.add_argument("--out", help="where to write it (default: in place, with a backup)")
    parser.add_argument("--dry", action="store_true")
    args = parser.parse_args()

    path = args.maps if os.path.isabs(args.maps) else os.path.join(HFDIR, args.maps)
    with open(path, encoding="utf-8") as f:
        document = json.load(f)

    sheets_dir = os.path.join(os.path.dirname(path), "sheets")
    on_disk = set(os.listdir(sheets_dir)) if os.path.isdir(sheets_dir) else set()
    lower_disk = {name.lower(): name for name in on_disk}
    source_of = document.get("artSrc") or {}
    art = document.get("artPng") or {}

    relinked, already, missing, no_source = 0, 0, [], []
    rebuilt = {}
    for sheet in sorted(set(art) | set(source_of)):
        current = os.path.basename(str(art.get(sheet) or ""))
        if current and current in on_disk:
            rebuilt[sheet] = "sheets/" + current
            already += 1
            continue
        source = source_of.get(sheet)
        if not source:
            no_source.append(sheet)
            continue
        candidate = art_file_name(sheet, str(source))
        if candidate in on_disk:
            rebuilt[sheet] = "sheets/" + candidate
            relinked += 1
            continue
        # Sheet names are case sensitive and Windows file names are not, so a sheet written
        # Lighthouse_TileSheet in one map and Lighthouse_Tilesheet in another lands on one file.
        # Matching case insensitively here costs nothing and recovers those.
        loose = lower_disk.get(candidate.lower())
        if loose:
            rebuilt[sheet] = "sheets/" + loose
            relinked += 1
        else:
            missing.append((sheet, candidate))

    claimed = {os.path.basename(v) for v in rebuilt.values()}
    orphans = on_disk - claimed

    print(f"art entries in     : {len(art)}")
    print(f"  already correct  : {already}")
    print(f"  relinked         : {relinked}")
    print(f"  no artSrc to use : {len(no_source)}")
    print(f"  file not on disk : {len(missing)}")
    print(f"art entries out    : {len(rebuilt)}")
    print(f"PNGs on disk       : {len(on_disk)}, of which {len(orphans)} are claimed by nothing")
    for sheet, candidate in missing[:8]:
        print(f"    missing: {sheet} -> {candidate}")

    if args.dry:
        print("\ndry run, nothing written")
        return
    document["artPng"] = rebuilt
    out = args.out or path
    out = out if os.path.isabs(out) else os.path.join(HFDIR, out)
    if out == path:
        shutil.copy2(path, path + ".before-relink")
        print(f"\nbacked up to {os.path.basename(path)}.before-relink")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(document, f)
    print(f"written to {out} ({os.path.getsize(out) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
