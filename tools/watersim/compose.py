"""Compose (passes A-E) for one tile window, recording what each pass removed from the MARCH
channel. The march channel is what the reflection anchors on, so anything that eats it at tile
granularity is what makes a pixel-accurate shoreline render as steps."""
import sys, os
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from watersim import *

SUB = 16

def compose(loc, tx, ty, tw, th, drop_short_runs=True, label_fastpath=False):
    pw, ph = tw * SUB, th * SUB
    n = tw * th
    isw = [False] * n
    bits = [None] * n
    keep = [None] * n
    carveB = [None] * n
    carveF = [None] * n
    bigsolid = [False] * n
    animonly = [False] * n
    labelled = [False] * n

    backs = loc.layer_names("Back")
    fronts = loc.layer_names("Front") + loc.layer_names("AlwaysFront")

    for j in range(th):
        for i in range(tw):
            x, y = tx + i, ty + j
            k = j * tw + i
            isw[k] = loc.is_water_tile(x, y)

    ring = dilate8(dilate8(dilate8(isw, tw, th), tw, th), tw, th)

    for j in range(th):
        for i in range(tw):
            x, y = tx + i, ty + j
            k = j * tw + i
            b = None
            lab = None
            for ln in backs:
                c = loc.cell(ln, x, y)
                if c:
                    lab = label_bits(c[0], c[1])
                    if lab:
                        break
            if lab:
                labelled[k] = True
                lb, nW, nI, nF, nL = lab
                if nW + nI + nF + nL > (7 if isw[k] else 0):
                    if isw[k]:
                        keep[k] = lb
                    else:
                        b = lb
            elif not isw[k]:
                for ln in backs:
                    c = loc.cell(ln, x, y)
                    if not c:
                        continue
                    px = tile_art(c[0], c[1])
                    if px is None:
                        continue
                    anim = loc.is_anim(c[0], c[1])
                    if ring[k]:
                        adj = ((i > 0 and isw[k - 1]) or (i < tw - 1 and isw[k + 1])
                               or (j > 0 and isw[k - tw]) or (j < th - 1 and isw[k + tw]))
                        b = classify_bits(px, foam=anim and adj)
                    elif anim:
                        cb = classify_bits(px)
                        if sum(cb) >= 64:
                            b = cb; animonly[k] = True
                    break
            bits[k] = b

            # structure / carve inputs
            cnt = 0
            for ln in ["Buildings"] + loc.layer_names("Buildings"):
                c = loc.cell(ln, x, y)
                if not c:
                    continue
                px = tile_art(c[0], c[1])
                if px is None:
                    continue
                sb, sn = solid_bits(px)
                if sn:
                    carveB[k] = sb; cnt = max(cnt, sn)
                break
            for ln in fronts:
                c = loc.cell(ln, x, y)
                if not c:
                    continue
                px = tile_art(c[0], c[1])
                if px is None:
                    continue
                sb, sn = solid_bits(px)
                if sn:
                    carveF[k] = sb; cnt = max(cnt, sn)
                break
            bigsolid[k] = cnt >= 230

    # ---- Pass A ----
    eff = [False] * (pw * ph)
    for j in range(th):
        for i in range(tw):
            k = j * tw + i
            w = isw[k]; b = bits[k]
            for py in range(SUB):
                row = (j * SUB + py) * pw + i * SUB
                for px_ in range(SUB):
                    eff[row + px_] = w or (b is not None and b[py * SUB + px_])

    # label subtraction on true water tiles (effect only, as shipped)
    for j in range(th):
        for i in range(tw):
            kp = keep[j * tw + i]
            if kp is None:
                continue
            for py in range(SUB):
                row = (j * SUB + py) * pw + i * SUB
                for px_ in range(SUB):
                    if not kp[py * SUB + px_]:
                        eff[row + px_] = False

    march = eff[:]                       # shipped order: copy BEFORE subtraction; see notes
    stages = {}

    def snap(name):
        stages[name] = march[:]

    snap("A")

    def close_vertical(buf, maxgap, speck_aware=False):
        for x in range(pw):
            last, runh = -99, 0
            for y in range(ph):
                if not buf[y * pw + x]:
                    continue
                gap = y - last - 1
                if speck_aware:
                    if gap == 0:
                        runh += 1
                    elif gap <= maxgap and (gap <= 4 or runh >= 3):
                        for kk in range(last + 1, y):
                            buf[kk * pw + x] = True
                        runh += gap + 1
                    else:
                        runh = 1
                else:
                    if 1 < y - last <= maxgap + 1:
                        for kk in range(last + 1, y):
                            buf[kk * pw + x] = True
                last = y

    close_vertical(march, 12, speck_aware=True)
    snap("close12")

    # structure scrub (whole tile) + carve
    for j in range(th):
        for i in range(tw):
            k = j * tw + i
            if not bigsolid[k]:
                continue
            if label_fastpath and labelled[k]:
                continue
            for py in range(SUB):
                row = (j * SUB + py) * pw + i * SUB
                for px_ in range(SUB):
                    march[row + px_] = False
    snap("structTile")

    # Pass D: per-column run tops, dropping short runs
    edge = [0] * (pw * ph)
    for x in range(pw):
        top, inrun = 0, False
        for y in range(ph + 1):
            p = y * pw + x
            if y < ph and march[p]:
                if not inrun:
                    inrun = True; top = y
                edge[p] = top
            elif inrun:
                if drop_short_runs and y < ph and y - top < 6:
                    for kk in range(top, y):
                        march[kk * pw + x] = False
                inrun = False
    snap("runDrop")
    return dict(eff=eff, march=march, edge=edge, stages=stages, pw=pw, ph=ph,
                tw=tw, th=th, isw=isw, labelled=labelled)


def render(res, path, title=""):
    pw, ph = res["pw"], res["ph"]
    Z = 2
    im = Image.new("RGB", (pw * Z, ph * Z), (18, 18, 22))
    px = im.load()
    eff, march, edge = res["eff"], res["march"], res["edge"]
    for y in range(ph):
        for x in range(pw):
            p = y * pw + x
            if march[p]:
                d = y - edge[p]
                col = (120, 255, 120) if d <= 4 else (0, min(255, 60 + d * 3), 255)
            elif eff[p]:
                col = (255, 130, 0)
            else:
                col = (18, 18, 22)
            for a in range(Z):
                for b in range(Z):
                    px[x * Z + a, y * Z + b] = col
    # tile grid
    for y in range(0, ph, SUB):
        for x in range(pw * Z):
            px[x, y * Z] = (90, 90, 110)
    for x in range(0, pw, SUB):
        for y in range(ph * Z):
            px[x * Z, y] = (90, 90, 110)
    im.save(path)
    print("wrote", path, title)
