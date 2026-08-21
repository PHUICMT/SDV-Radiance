"""Point every art reference at the spelling the file really has on disk.

Two mods can spell one sheet's name with different capitalisation. Windows treats those as one
file, so the second dump found the first's PNG already there, wrote nothing, and recorded its own
spelling. The reference is then correct on Windows and a 404 anywhere case matters, which includes
the browser reading this corpus through a directory handle.

MapDump stopped creating these (it now reads back the on-disk name), but a corpus built before
that still carries them and is far too expensive to re-dump for three strings.

    python tools/labelops/fixsheetcase.py            say what is wrong
    python tools/labelops/fixsheetcase.py --write    fix it
"""
import io, json, os, shutil, sys

sys.stdout.reconfigure(encoding="utf-8")
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
INDEX = os.path.join(HFDIR, "maps.json")
SHEETS = os.path.join(HFDIR, "sheets")
WRITE = "--write" in sys.argv


def load(path):
    with io.open(path, encoding="utf-8") as handle:
        return json.load(handle)


def save(path, document):
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, ensure_ascii=False, separators=(",", ":"))


real_name = {}
for name in os.listdir(SHEETS):
    real_name[name.lower()] = name


def corrected(value):
    """The same reference with the file's real capitalisation, or None if it is already right."""
    text = str(value)
    leaf = text.replace("\\", "/").split("/")[-1]
    if leaf in real_name.values():
        return None
    actual = real_name.get(leaf.lower())
    if actual is None or actual == leaf:
        return None
    return text[: len(text) - len(leaf)] + actual


document = load(INDEX)
fixes = 0

for field in ("artPng", "artPngBySrc"):
    table = document.get(field) or {}
    for key, value in list(table.items()):
        fixed = corrected(value)
        if fixed:
            print(f"  {field:14} {value}  ->  {fixed}")
            table[key] = fixed
            fixes += 1

# ...and the per-location files, which carry their own sheetArt map
touched_files = 0
for entry in document["locations"].values():
    path = entry.get("file")
    if not path:
        continue
    full = os.path.join(HFDIR, path)
    try:
        sub = load(full)
    except OSError:
        continue
    # sheetArt is a LIST running parallel to the location's `sheets`, with a null wherever no art
    # was captured for that slot. It was a dict in an earlier layout, so both are read.
    art = sub.get("sheetArt")
    changed = False
    if isinstance(art, dict):
        slots = list(art.items())
    elif isinstance(art, list):
        slots = list(enumerate(art))
    else:
        continue
    for slot, value in slots:
        if value is None:
            continue
        fixed = corrected(value)
        if fixed:
            art[slot] = fixed
            changed = True
            fixes += 1
    if changed:
        touched_files += 1
        if WRITE:
            save(full, sub)

print(f"\n{fixes} reference(s) name a file whose real spelling differs, "
      f"across the index and {touched_files} location file(s)")
if not fixes:
    print("nothing to do")
elif WRITE:
    backup = INDEX + ".before-casefix"
    if not os.path.exists(backup):
        shutil.copy2(INDEX, backup)
    save(INDEX, document)
    print(f"written. the index as it was: {os.path.basename(backup)}")
else:
    print("nothing written; pass --write to fix")
