"""Which sheet does WHICH map place, read from the .tmx files on disk.

The dump can only say "a loaded map placed this sheet", so 4,546 sheets looked context-less
purely because their mod was parked. But a .tmx names its tilesheets in plain text, and every
mod's .tmx is sitting right there whether the game loaded it or not - so the map a sheet
belongs to can be recovered without enabling anything.

Colour was the wrong signal for this: sky, panoramas and mod-page preview images are all blue,
and they topped the "has liquid" list while being nothing to do with water.
"""
import json, os, re, sys
from collections import defaultdict
sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
H = os.path.expanduser(r"~\Documents\HF-Studio")
ROOTS = [os.path.join(GAME, "Mods"), os.path.join(GAME, "Mods (disabled)")]


def norm(src):
    n = src.replace("\\", "/").split("/")[-1]
    return n[:-4] if n.lower().endswith(".png") else n


# sheet name -> the .tmx files that reference it
by_sheet = defaultdict(set)
tmx_count = 0
for root in ROOTS:
    if not os.path.isdir(root):
        continue
    for dp, _dn, fns in os.walk(root):
        for fn in fns:
            if not fn.lower().endswith((".tmx", ".tsx")):
                continue
            path = os.path.join(dp, fn)
            try:
                raw = open(path, encoding="utf-8", errors="replace").read(400_000)
            except OSError:
                continue
            tmx_count += 1
            for m in re.finditer(r'<image[^>]+source="([^"]+)"', raw):
                by_sheet[norm(m.group(1))].add(fn[:-4])

print(f"{tmx_count} .tmx/.tsx files on disk reference {len(by_sheet)} distinct sheets\n")

d = json.load(open(os.path.join(H, "maps.json"), encoding="utf-8"))
art = d.get("artPng", {})
placed_live = set()
for L in d["locations"].values():
    placed_live.update(L["sheets"])

# game ground truth: sheets the game itself has called water somewhere
water_sheets = set(d.get("water", {}))

in_dump_ctx = in_tmx_only = no_ctx = 0
recoverable = []
for name in art:
    if name in placed_live:
        in_dump_ctx += 1
    elif name in by_sheet:
        in_tmx_only += 1
        recoverable.append((len(by_sheet[name]), name, sorted(by_sheet[name])[:3]))
    else:
        no_ctx += 1

print(f"of {len(art)} sheets in the tool:")
print(f"  a LOADED map places it            : {in_dump_ctx:5d}   context already in map mode")
print(f"  only a PARKED mod's .tmx uses it  : {in_tmx_only:5d}   context recoverable - the map exists")
print(f"  no .tmx anywhere references it    : {no_ctx:5d}   not a map tilesheet at all")
print(f"\n  the game calls water somewhere    : {len(water_sheets & set(art)):5d}   ground truth, no guessing")

recoverable.sort(reverse=True)
print("\nsheets used by the most maps (best value to label):")
for n, nm, where in recoverable[:20]:
    print(f"  {n:4d} maps  {nm[:44]:46s} e.g. {', '.join(w[:22] for w in where)}")

json.dump({"tmx_by_sheet": {k: sorted(v) for k, v in by_sheet.items()}},
          open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "sheet-context.json"), "w", encoding="utf-8"),
          ensure_ascii=False)
print("\nwrote sheet-context.json")
