"""Pack every label file into the one file the labeller's Import button accepts.

    python tools/labelops/bundlelabels.py            report what would go in
    python tools/labelops/bundlelabels.py --write    write labels-import.json

WHY THIS IS NEEDED, and it is not obvious.

The labeller keeps its labels in the browser's localStorage. The Live JSON folder is a mirror
OUT of it: opening a sheet and painting writes `<sheet>.labels.json` to disk, and nothing ever
reads those files back on its own. The Import button is the only way in, and it is a manual file
picker taking one file.

So a label written by a tool rather than by a hand - everything twinlabels.py --apply copies -
sits in a file the app has never seen. Worse than invisible: the app rewrites that whole file
from its own state the next time the sheet is saved, so an unimported label is deleted by the
next brush stroke on the same sheet.

One file with every sheet in it, imported once, ends that. The importer merges per tile rather
than replacing a sheet, so importing is safe to repeat and cannot undo newer painting.
"""
import argparse, base64, collections, glob, io, json, os, sys

sys.stdout.reconfigure(encoding="utf-8")

HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
# Named to sort to the TOP of a file dialog. The folder holds 286 <sheet>.labels.json
# files that cannot move, so a name starting with a letter lands in the middle of them:
# "labels-import.json" sat between JojaPetStore and MallInterior.
OUT = os.path.join(HFDIR, "!labels-import.json")
LABELLER = os.environ.get("HF_LABELER_DIR") or \
           r"E:\Games\GamesMods\DevStardew\SDV-HeightFramework\tools\labeler"


def colour_guess():
    """The tiles data.js guessed, as sheet -> {tile: bytes}.

    data.js shipped a colour guess on 2026-07-21: 15,101 tiles over 21 sheets, every one of them
    called water because its pixels lean blue. The labeller no longer starts a sheet from it, but
    the label FILES on disk were written while it did, so the guess is sitting in them - and
    importing those files would put it back as though a person had meant it. Read here to be left
    out again.
    """
    path = os.path.join(LABELLER, "data.js")
    try:
        with io.open(path, encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        head = "window.LABELER_DATA="
        payload = json.loads(text[text.index(head) + len(head):].rstrip().rstrip(";"))
    except (OSError, ValueError):
        return {}
    out = {}
    for sheet, entry in (payload.get("labels") or {}).items():
        name = sheet.split("__", 1)[-1]
        tiles = {}
        for tile, blob in ((entry or {}).get("tiles") or {}).items():
            raw = base64.b64decode(blob)
            if any(raw):
                tiles[tile] = raw
        if tiles:
            out[name] = tiles
    return out


def label_files():
    """sheet -> newest file holding its labels. The live folder wins over labels/, which is
    where an older copy can sit."""
    found = {}
    for folder in (HFDIR, os.path.join(HFDIR, "labels")):
        for path in sorted(glob.glob(os.path.join(folder, "*.labels.json"))):
            sheet = os.path.basename(path)[:-len(".labels.json")]
            found.setdefault(sheet, path)
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--keep-guess", action="store_true",
                        help="carry the 2026-07-21 colour guess across too (it is left out by default)")
    arguments = parser.parse_args()

    guess = {} if arguments.keep_guess else colour_guess()
    sheets, painted, ground_only, unreadable, dropped = {}, 0, 0, 0, 0
    classes = None
    for sheet, path in sorted(label_files().items()):
        try:
            with io.open(path, encoding="utf-8") as handle:
                document = json.load(handle)
        except (OSError, ValueError):
            unreadable += 1
            continue
        tiles = document.get("tiles") or {}
        if not tiles:
            continue
        # A tile still byte-for-byte what the guess put there is nobody's answer. One a person
        # edited differs from it and stays, which is the whole reason this compares bytes rather
        # than dropping every sheet the guess touched.
        guessed = guess.get(sheet) or {}
        if guessed:
            keep = {tile: blob for tile, blob in tiles.items()
                    if base64.b64decode(blob) != guessed.get(tile)}
            dropped += len(tiles) - len(keep)
            tiles = keep
        if not tiles:
            continue
        classes = classes or document.get("classes")
        # A tile of all zero bytes is the file saying "ground here", which the importer records
        # as intent rather than as pixels. Both kinds are carried: dropping the empty ones would
        # lose the answer "somebody looked at this tile and it is nothing".
        marked = sum(1 for blob in tiles.values() if any(base64.b64decode(blob)))
        painted += marked
        ground_only += len(tiles) - marked
        sheets[sheet] = {"tiles": tiles}

    print(f"{len(sheets)} sheet(s) with labels")
    print(f"  tiles carrying a class : {painted:,}")
    print(f"  tiles marked as ground : {ground_only:,}")
    if unreadable:
        print(f"  unreadable files       : {unreadable}")
    if dropped:
        print(f"  left out, still the colour guess untouched: {dropped:,}")

    if not arguments.write:
        print("\npass --write to build the file")
        return

    payload = {"//": "Every label file packed into one, for the labeller's Import button. The "
                     "importer merges per tile, so importing this cannot undo newer painting. "
                     "Written by tools/labelops/bundlelabels.py.",
               "format": "16x16-classes-base64", "sheets": sheets}
    if classes:
        payload["classes"] = classes
    with io.open(OUT, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False)
    print(f"\nwrote {OUT}  ({os.path.getsize(OUT) / 1048576:.1f} MB)")
    print("in the labeller: Import -> pick this file. Once, before painting again.")


if __name__ == "__main__":
    main()
