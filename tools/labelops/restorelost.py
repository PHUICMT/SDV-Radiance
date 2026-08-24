"""Put back labels a bundle has and the live files no longer do.

    python tools/labelops/restorelost.py            say what is missing
    python tools/labelops/restorelost.py --write    put it back

WHY THIS EXISTS. A sheet NAME can have pictures of different sizes: spring_beach is 272x496 in
the base game and 4096x4096 in a pack that ships one big sheet, and thirty names in this corpus
are like that. The labeller stores labels per name, so opening the tall picture and saving wrote
the store from it - and every tile past the short picture's last row went with it. 254 tiles
across the four seasonal beach sheets, in one sitting.

The guard that stops it is in persistSheet now. This is the other half: the bundle written before
that sitting still holds them, and a tile that is in the bundle and gone from the file is exactly
what was lost.

It only ADDS. A tile the file already carries is left alone whatever the bundle says, because the
file is the newer of the two and the whole point is to lose nothing.
"""
import argparse, base64, io, json, os, shutil, sys, time

sys.stdout.reconfigure(encoding="utf-8")

HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
BUNDLE = os.path.join(HFDIR, "!labels-import.json")


def painted(blob):
    try:
        return any(base64.b64decode(blob))
    except Exception:
        return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--bundle", default=BUNDLE)
    arguments = parser.parse_args()

    if not os.path.exists(arguments.bundle):
        sys.exit(f"no bundle at {arguments.bundle}")
    with io.open(arguments.bundle, encoding="utf-8") as handle:
        sheets = json.load(handle).get("sheets") or {}
    print(f"bundle: {arguments.bundle}")
    print(f"        written {time.ctime(os.path.getmtime(arguments.bundle))}")

    plan, total = [], 0
    for sheet, entry in sorted(sheets.items()):
        path = os.path.join(HFDIR, sheet + ".labels.json")
        if not os.path.exists(path):
            path = os.path.join(HFDIR, "labels", sheet + ".labels.json")
        if not os.path.exists(path):
            continue
        try:
            with io.open(path, encoding="utf-8") as handle:
                document = json.load(handle)
        except (OSError, ValueError):
            continue
        now = document.get("tiles") or {}
        missing = {tile: blob for tile, blob in (entry.get("tiles") or {}).items()
                   if painted(blob) and not painted(now.get(tile, ""))}
        if missing:
            plan.append((sheet, path, document, missing))
            total += len(missing)

    print(f"\ntiles the bundle has and the files do not: {total:,} on {len(plan)} sheet(s)")
    for sheet, _, _, missing in sorted(plan, key=lambda row: -len(row[3]))[:12]:
        print(f"   {len(missing):>5}  {sheet}")

    if not arguments.write:
        print("\npass --write to put them back")
        return
    if not plan:
        print("nothing to restore")
        return

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = os.path.join(HFDIR, f"_labels-before-restore-{stamp}")
    os.makedirs(backup, exist_ok=True)
    written = 0
    for sheet, path, document, missing in plan:
        shutil.copy2(path, os.path.join(backup, os.path.basename(path)))
        document.setdefault("tiles", {}).update(missing)
        with io.open(path, "w", encoding="utf-8") as handle:
            json.dump(document, handle, ensure_ascii=False)
        written += len(missing)
    print(f"\nput back {written:,} tile(s); the files they replaced are in {backup}")
    print("in the labeller: Import the bundle once, so the browser has them too.")


if __name__ == "__main__":
    main()
