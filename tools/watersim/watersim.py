"""
Offline replica of RenderPipeline.WaterMask.Async.ComposeWaterMask.

The point is to see the intermediate state. In the game the only readout is a screenshot after a
rebuild + relaunch + walk, which is why six hypotheses in a row survived longer than they deserved.
Everything the compose needs is already on disk: HF Studio's maps.json carries every location's
layers AND all 307 tilesheets as base64 PNGs, and the label set ships with the mod.

Renders, per pass, what that pass REMOVED, so a tile-granular pass eating a pixel-accurate
shoreline is visible instead of inferred.
"""
import json, base64, io, sys, os
from PIL import Image

HF = r"C:/Users/icmtc/Documents/HF-Studio/maps.json"
REPO = r"e:/Games/GamesMods/DevStardew/SDV-Radiance"
OUT = os.path.dirname(os.path.abspath(__file__))
SUB = 16

_D = None
def dump():
    global _D
    if _D is None:
        _D = json.load(open(HF, encoding="utf-8"))
    return _D

_sheets = {}
def sheet_img(name):
    if name not in _sheets:
        b64 = dump()["art"].get(name)
        _sheets[name] = Image.open(io.BytesIO(base64.b64decode(b64.split(",", 1)[1]))).convert("RGBA") if b64 else None
    return _sheets[name]

def tile_art(sheetname, idx):
    """16x16 RGBA pixel list for one tile, or None."""
    im = sheet_img(sheetname)
    if im is None:
        return None
    cols = im.width // 16
    if cols == 0:
        return None
    x, y = (idx % cols) * 16, (idx // cols) * 16
    if y + 16 > im.height:
        return None
    return list(im.crop((x, y, x + 16, y + 16)).getdata())

# ---- ports of the C# classifiers (RenderPipeline.WaterMask.cs) ----

def water_color(c):
    r, g, b, a = c
    if a < 200:
        return False
    if b > r + 14 and b + 10 >= g:
        return True
    return g > r + 10 and b > r + 12 and b >= g - 20

def classify_bits(px, foam=False):
    out = [False] * 256
    for p, c in enumerate(px):
        w = water_color(c)
        if not w and foam and c[3] >= 200:
            mx, mn = max(c[:3]), min(c[:3])
            w = mx >= 190 and mx - mn <= 25 and c[2] >= c[0]
        out[p] = w
    return out

def shadow_wash(c):
    return c[3] < 250 and max(c[:3]) <= 40

def solid_bits(px):
    bits = [False] * 256
    n = w = 0
    for p, c in enumerate(px):
        v = c[3] >= 128 and not shadow_wash(c)
        bits[p] = v
        if v:
            n += 1
            if water_color(c):
                w += 1
    if w * 10 >= n * 6:          # >=60% of the opaque art is water -> overlay, not structure
        return [False] * 256, 0
    return bits, n

# ---- location access ----

class Loc:
    def __init__(self, name):
        d = dump()
        L = d["locations"][name]
        self.name = name
        self.sheets = L["sheets"]
        self.layers = {}
        for l in L["layers"]:
            self.layers[l["id"]] = (l["w"], l["h"], base64.b64decode(l["cells"]))
        self.W = max(v[0] for v in self.layers.values())
        self.H = max(v[1] for v in self.layers.values())
        self.waterset = {k.lower(): set(v) for k, v in (d.get("water") or {}).items()}
        self.anim = set(x.lower() for g in (d.get("animGroups") or []) for x in g)

    def cell(self, layer, x, y):
        e = self.layers.get(layer)
        if not e:
            return None
        w, h, buf = e
        if not (0 <= x < w and 0 <= y < h):
            return None
        o = (y * w + x) * 4
        v = int.from_bytes(buf[o:o + 4], "little", signed=True)
        if v < 0:
            return None
        si = v // 0x100000
        if si >= len(self.sheets):
            return None
        return self.sheets[si], v % 0x100000

    def layer_names(self, prefix):
        return [k for k in self.layers if k.startswith(prefix)]

    def is_water_tile(self, x, y):
        for ln in self.layer_names("Back"):
            c = self.cell(ln, x, y)
            if c and c[1] in self.waterset.get(c[0].lower(), ()):
                return True
        return False

    def is_anim(self, sheet, idx):
        return f"{sheet.lower()}:{idx}" in self.anim

_labels = None
def labels():
    global _labels
    if _labels is None:
        p = os.path.join(REPO, "labels", "water-labels.json")
        _labels = json.load(open(p, encoding="utf-8"))["sheets"]
    return _labels

def label_bits(sheet, idx):
    """(waterbits, nWater, nIce, nFlow, nLava) or None when the tile carries no label."""
    s = labels().get(sheet)
    if not s:
        for k in labels():
            if k.lower() == sheet.lower():
                s = labels()[k]
                break
    if not s:
        return None
    raw = (s.get("tiles") or {}).get(str(idx))
    if raw is None:
        return None
    cls = base64.b64decode(raw)
    bits = [False] * 256
    nW = nI = nF = nL = 0
    for p in range(256):
        c = cls[p]
        if c == 1:
            bits[p] = True; nW += 1
        elif c == 9:
            bits[p] = True; nI += 1
        elif c == 10:
            bits[p] = True; nF += 1
        elif c == 11:
            bits[p] = True; nL += 1
    return bits, nW, nI, nF, nL

def dilate8(src, w, h):
    dst = [False] * (w * h)
    for j in range(h):
        for i in range(w):
            v = False
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    xx, yy = i + dx, j + dy
                    if 0 <= xx < w and 0 <= yy < h and src[yy * w + xx]:
                        v = True; break
                if v: break
            dst[j * w + i] = v
    return dst
