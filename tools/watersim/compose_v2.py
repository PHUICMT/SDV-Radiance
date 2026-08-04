"""Proposal: when a tile carries a label, decide PER PIXEL and skip the tile-level verdicts.

The shipped compose asks a chain of "how much?" questions (opaque >= 230, label liquid >= 128,
colour-water >= 60%, run < 6 texels) and each answer is a whole-tile yes/no. Those constants are
fitted guesses, and a whole-tile verdict is what quantises a pixel-accurate shoreline into steps.

With a label there is nothing to guess: a pier deck has zero liquid pixels painted on it, a wave
line has ninety-four. Carve exactly the opaque pixels the label does not call liquid and the
structure test, its threshold, and the whole-tile scrub all become unnecessary at once.

Unlabelled tiles keep the old path, so nothing regresses on maps nobody has painted.
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from watersim import *

SUB = 16

def compose_v2(loc, tx, ty, tw, th, per_pixel=True, drop_short=False):
    pw, ph = tw * SUB, th * SUB
    n = tw * th
    backs = loc.layer_names("Back")
    overlays = ["Buildings"] + [l for l in loc.layer_names("Buildings") if l != "Buildings"] \
               + loc.layer_names("Front") + loc.layer_names("AlwaysFront")

    isw = [loc.is_water_tile(tx + i, ty + j) for j in range(th) for i in range(tw)]
    ring = dilate8(dilate8(dilate8(isw, tw, th), tw, th), tw, th)

    eff = [False] * (pw * ph)
    labelled = [False] * n

    for j in range(th):
        for i in range(tw):
            k = j * tw + i
            x, y = tx + i, ty + j
            base = [isw[k]] * 256

            # --- Back: label first, colour only as fallback ---
            lab = None
            for ln in backs:
                c = loc.cell(ln, x, y)
                if c:
                    lab = label_bits(c[0], c[1])
                    if lab:
                        break
            if lab:
                labelled[k] = True
                lb = lab[0]
                if isw[k]:
                    base = lb[:]                      # label REPLACES the square, both channels
                else:
                    base = lb[:]
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
                        base = classify_bits(px, foam=anim and adj)
                    elif anim:
                        cb = classify_bits(px)
                        base = cb if sum(cb) >= 64 else [False] * 256
                    break

            # --- overlays: carve the opaque pixels the label does NOT call liquid ---
            for ln in overlays:
                c = loc.cell(ln, x, y)
                if not c:
                    continue
                px = tile_art(c[0], c[1])
                if px is None:
                    continue
                ol = label_bits(c[0], c[1])
                if ol is not None:
                    labelled[k] = True
                if per_pixel and ol is not None:
                    olb = ol[0]
                    for p in range(256):
                        if px[p][3] >= 128 and not shadow_wash(px[p]):
                            if olb[p]:
                                base[p] = True        # label says liquid -> it IS water
                            else:
                                base[p] = False       # painted structure -> carve this pixel only
                else:
                    sb, cnt = solid_bits(px)
                    if per_pixel:
                        for p in range(256):
                            if sb[p]:
                                base[p] = False
                    else:
                        if cnt >= 230:
                            base = [False] * 256      # shipped: whole-tile verdict
                        else:
                            for p in range(256):
                                if sb[p]:
                                    base[p] = False

            for py in range(SUB):
                row = (j * SUB + py) * pw + i * SUB
                for pxi in range(SUB):
                    eff[row + pxi] = base[py * SUB + pxi]

    march = eff[:]

    # islands and pads must not become shorelines: hand back holes the outside cannot reach
    outside = [False] * (pw * ph)
    st = []
    for x in range(pw):
        for p in (x, (ph - 1) * pw + x):
            if not march[p] and not outside[p]:
                outside[p] = True; st.append(p)
    for y in range(ph):
        for p in (y * pw, y * pw + pw - 1):
            if not march[p] and not outside[p]:
                outside[p] = True; st.append(p)
    while st:
        p = st.pop(); x, y = p % pw, p // pw
        for q, ok in ((p - 1, x > 0), (p + 1, x < pw - 1), (p - pw, y > 0), (p + pw, y < ph - 1)):
            if ok and not march[q] and not outside[q]:
                outside[q] = True; st.append(q)
    for p in range(pw * ph):
        if not march[p] and not outside[p]:
            march[p] = True

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
                if drop_short and y < ph and y - top < 6:
                    for kk in range(top, y):
                        march[kk * pw + x] = False
                inrun = False
    return dict(eff=eff, march=march, edge=edge, pw=pw, ph=ph, tw=tw, th=th,
                isw=isw, labelled=labelled)


def anchor_stats(res):
    pw, ph, m, e = res["pw"], res["ph"], res["march"], res["edge"]
    tops = []
    for x in range(pw):
        t = None
        for y in range(ph):
            if m[y * pw + x]:
                t = e[y * pw + x]; break
        tops.append(t)
    v = [t for t in tops if t is not None]
    ch = sum(1 for a, b in zip(v, v[1:]) if a != b)
    big = sum(1 for a, b in zip(v, v[1:]) if abs(a - b) >= 8)
    return len(v), len(set(v)), ch, big
