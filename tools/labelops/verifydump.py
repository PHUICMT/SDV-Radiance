"""Verify a fresh maps.json against the .tmx files it came from: cells AND orientation.

Ground truth is the .tmx gid (index + flip bits), decoded here independently of the mod.
Only maps loaded wholesale from a .tmx are comparable - a map CP patches with EditMap will
legitimately differ, so those are reported separately rather than counted as errors.
"""
import base64, io, json, os, struct, sys
import xml.etree.ElementTree as ET
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HF = os.path.expanduser(r"~\Documents\HF-Studio\maps.json")
H, V, D = 0x80000000, 0x40000000, 0x20000000
EXPECT = {(0,0,0):0, (1,0,0):4, (0,1,0):6, (1,1,0):2,
          (0,0,1):7, (1,0,1):1, (0,1,1):3, (1,1,1):5}


def norm(src):
    n = src.replace("\\", "/").split("/")[-1]
    return n[:-4] if n.lower().endswith(".png") else n


def load_tmx(path):
    root = ET.parse(path).getroot()
    base = os.path.dirname(path)
    ts = []
    for t in root.findall("tileset"):
        fg = int(t.get("firstgid"))
        if t.get("source"):
            sp = os.path.join(base, t.get("source"))
            if not os.path.exists(sp):
                return None
            sub = ET.parse(sp).getroot()
            img, cnt = sub.find("image").get("source"), int(sub.get("tilecount"))
        else:
            im = t.find("image")
            if im is None:
                return None
            img, cnt = im.get("source"), int(t.get("tilecount"))
        ts.append((fg, norm(img), cnt))
    ts.sort()
    layers = {}
    for lay in root.findall("layer"):
        data = lay.find("data")
        if data is None or data.get("encoding") != "csv" or not data.text:
            continue
        try:
            gids = [int(v) for v in data.text.replace("\n", "").split(",") if v.strip()]
        except ValueError:
            continue
        layers[lay.get("name")] = (int(lay.get("width")), int(lay.get("height")), gids)
    return ts, layers


def resolve(ts, gid):
    flags = (1 if gid & H else 0, 1 if gid & V else 0, 1 if gid & D else 0)
    g = gid & 0x1FFFFFFF
    if g == 0:
        return None
    for fg, n, c in reversed(ts):
        if g >= fg:
            return n, g - fg, flags
    return None


tmx_by_name = {}
for dp, _, fns in os.walk(os.path.join(GAME, "Mods")):
    for fn in fns:
        if fn.lower().endswith(".tmx"):
            tmx_by_name.setdefault(fn[:-4], []).append(os.path.join(dp, fn))

D_ = json.load(open(HF, encoding="utf-8"))
locs = D_["locations"]
HFDIR = os.path.dirname(HF)
print(f"dump: {len(locs)} version(s) of {len({v.get('name', k) for k, v in locs.items()})} place(s), "
      f"{len(D_.get('artPng', {}))} sheet art file(s), season={D_.get('season')}")


def layers_of(entry):
    """A version's layer data, which v3 keeps in its own file beside the index.

    The index holds only what is needed to tell versions apart, because the dump now carries
    thousands of them and every byte of a location is repeated per version otherwise.
    """
    path = entry.get("file")
    if not path:
        return entry.get("layers") or []          # a v1 document, read in place
    try:
        with io.open(os.path.join(HFDIR, path), encoding="utf-8") as handle:
            return json.load(handle).get("layers") or []
    except OSError:
        return []

checked = cell_bad = ori_bad = cells_tot = turned_tot = 0
skipped = []
detail = Counter()
for verkey, L in locs.items():
    locname = L.get("name", verkey)
    cands = tmx_by_name.get(locname)
    if not cands:
        continue
    parsed = load_tmx(cands[0])
    if not parsed:
        continue
    ts, tlayers = parsed
    dumped_layers = layers_of(L)
    if not any(l["id"] in tlayers for l in dumped_layers):
        continue
    cb = ob = 0
    for lay in dumped_layers:
        if lay["id"] not in tlayers:
            continue
        w, h, gids = tlayers[lay["id"]]
        if w != lay["w"] or h != lay["h"]:
            continue
        raw = base64.b64decode(lay["cells"])
        cells = struct.unpack("<%di" % (len(raw)//4), raw)
        ori = base64.b64decode(lay["orient"]) if lay.get("orient") else bytes(w*h)
        anim = set(lay.get("anim") or [])
        sheets = L["sheets"]
        for i, gid in enumerate(gids):
            t = resolve(ts, gid)
            dv = cells[i]
            if t is None:
                continue
            cells_tot += 1
            name, idx, flags = t
            if dv >= 0 and (sheets[dv >> 20] != name or (dv & 0xFFFFF) != idx and i not in anim):
                cb += 1
            if flags != (0, 0, 0):
                turned_tot += 1
            if ori[i] != EXPECT[flags]:
                ob += 1
                detail[(flags, ori[i], EXPECT[flags])] += 1
    if cb or ob:
        # a CP EditMap patch legitimately changes cells; orientation errors are ours
        skipped.append((verkey, cb, ob))
    checked += 1
    cell_bad += cb
    ori_bad += ob

print(f"\ncompared {checked} locations against their .tmx source")
print(f"  cells checked : {cells_tot:,}")
print(f"  turned tiles  : {turned_tot:,}")
print(f"  CELL mismatches       : {cell_bad:,}")
print(f"  ORIENTATION mismatches: {ori_bad:,}")
if skipped:
    print("\nlocations with any mismatch (cell diffs can be CP EditMap patches):")
    for n, cb, ob in sorted(skipped, key=lambda s: -(s[1]+s[2]))[:15]:
        print(f"  {n:52s} cells={cb:5d} orient={ob:5d}")
if detail:
    print("\norientation mismatch detail (tmx HVD, got, expected):")
    for k, n in detail.most_common(10):
        print(f"  H={k[0][0]} V={k[0][1]} D={k[0][2]}  got {k[1]}  expected {k[2]}   x{n}")
