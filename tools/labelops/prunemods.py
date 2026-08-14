"""Which downloaded mods actually give the labeller a sheet, and which are dead weight.

The test is MapDump.AddUnplacedSheetArt's, not a new one: a PNG at least 128px on both sides
with both dimensions a multiple of 16, in a folder whose name does not say it holds people
rather than places. A mod with no such file can never appear in sheet mode, so downloading it
was wasted and keeping it only costs disk.

    python prunemods.py            report only
    python prunemods.py --delete   remove the folders that contribute nothing
"""
import os, shutil, sys
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PARK = os.path.join(GAME, "Mods (disabled)")
CATS = ["09_Buildings", "10_Locations", "11_Maps", "12_Visuals"]
NOT_PLACES = ("portrait", "character", "animals", "fashion", "\\ui", "icon", "emoji",
              "hair", "shirt", "pants", "hats", "shoes", "tattoo", "bodies",
              "\\shots\\", "screenshot", "\\shot_")


def sheets_in(mod_dir):
    """Count PNGs that MapDump would embed, and the tmx/tbin maps the mod ships."""
    sheets = maps = 0
    head = bytearray(24)
    for dp, _dn, fns in os.walk(mod_dir):
        low = ("\\" + os.path.relpath(dp, mod_dir).lower() + "\\").replace("/", "\\")
        skip_dir = any(bad in low for bad in NOT_PLACES)
        for fn in fns:
            l = fn.lower()
            if l.endswith((".tmx", ".tbin")):
                maps += 1
                continue
            if not l.endswith(".png") or skip_dir:
                continue
            if any(bad.strip("\\") in l for bad in NOT_PLACES):
                continue
            try:
                with open(os.path.join(dp, fn), "rb") as fh:
                    if fh.readinto(head) < 24:
                        continue
                if head[1:4] != b"PNG":
                    continue
                w = int.from_bytes(head[16:20], "big")
                h = int.from_bytes(head[20:24], "big")
                if w >= 128 and h >= 128 and w % 16 == 0 and h % 16 == 0:
                    sheets += 1
            except OSError:
                continue
    return sheets, maps


def size_of(p):
    n = 0
    for dp, _dn, fns in os.walk(p):
        for f in fns:
            try:
                n += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return n


delete = "--delete" in sys.argv
keep_n = drop_n = 0
keep_b = drop_b = 0
dropped = []
for cat in CATS:
    root = os.path.join(PARK, cat)
    if not os.path.isdir(root):
        continue
    ck = cd = 0
    for mod in sorted(os.listdir(root)):
        p = os.path.join(root, mod)
        if not os.path.isdir(p):
            continue
        sheets, maps = sheets_in(p)
        b = size_of(p)
        if sheets or maps:
            keep_n += 1; keep_b += b; ck += 1
        else:
            drop_n += 1; drop_b += b; cd += 1
            dropped.append((cat, mod, b))
            if delete:
                shutil.rmtree(p, ignore_errors=True)
    print(f"  {cat:14s} keep {ck:4d}   nothing to label {cd:4d}")

print(f"\nkeep {keep_n} mods ({keep_b/1e9:.2f} GB)")
print(f"{'deleted' if delete else 'would drop'} {drop_n} mods ({drop_b/1e9:.2f} GB)")
if not delete and dropped:
    print("\nbiggest with nothing labellable:")
    for cat, mod, b in sorted(dropped, key=lambda r: -r[2])[:20]:
        print(f"  {b/1e6:7.1f} MB  {cat}/{mod[:56]}")
