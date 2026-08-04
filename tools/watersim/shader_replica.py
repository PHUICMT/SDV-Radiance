"""Faithful replica of the CURRENT water.fx mirror block (branch feat/water-v3 @ 0fa964e) over a
faithfully-rebuilt mask, so the term that kills a reflection can be MEASURED instead of guessed.

Mask rebuild here mirrors the CURRENT C#, not the v2 proposal:
  - true water tiles: full square; label KEEP subtracts art from the EFFECT channel only
  - Buildings/Front overlay on water: carve minus label-liquid; bldLabeledLiquid on ANY liquid
  - _tileBigSolid = deck(manual) or (opaque>=230 and not labelledLiquid) or front>=230
  - structTile blocks march whole-tile when deck or unlabelled; labelled non-deck big art carves
    the march at its painted shape (pixelCarveMarch)
  - Pass D top-of-run per column; short-run drop <6; NO +-10 smoothing (noted; second-order here)

Shader terms replicated exactly: waterOff/edgeV/depth, reflUv = edgeV - depth*1.25 - 0.08/tps.y,
srcWater 5-tap, found, fade=1-depth*0.5, toSky = max(smoothstep(5,9,dT), srcW*smoothstep(2,4,dT)),
mirrorCol=refl*(0.66,0.76,0.92) -> lerp to skySurf, sheen=lerp(col,Sky,0.12),
amt = 0.71 * water * fade * onScreen * sat(srcLum*3.2) * lerp(0.5,1,found).
Ripple/wave/dither are cosmetic and omitted (static frame).
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from watersim import *
from PIL import Image

SUB = 16
TPSY = 16.875          # 1920x1080 zoom 1.0; band positions in TILES don't depend on this
SKY = (158, 199, 245)  # day SkyColor 0.62,0.78,0.96

def art_img(loc, tx, ty, tw, th):
    im = Image.new('RGBA', (tw*16, th*16), (0,0,0,255))
    for j in range(th):
        for i in range(tw):
            for ln in ['Back','Back2','Buildings','Buildings2','Front','AlwaysFront']:
                c = loc.cell(ln, tx+i, ty+j)
                if not c: continue
                s = sheet_img(c[0])
                if s is None: continue
                cols = s.width//16; x0,y0 = (c[1]%cols)*16, (c[1]//cols)*16
                if y0+16 > s.height: continue
                im.alpha_composite(s.crop((x0,y0,x0+16,y0+16)), (i*16, j*16))
    return im.convert('RGB')

def build_mask(loc, tx, ty, tw, th, deck_tiles=frozenset()):
    pw, ph = tw*SUB, th*SUB
    n = tw*th
    isw = [loc.is_water_tile(tx+i, ty+j) for j in range(th) for i in range(tw)]
    eff  = [False]*(pw*ph)
    march= [False]*(pw*ph)
    backs = loc.layer_names('Back')
    blds  = ['Buildings'] + [l for l in loc.layer_names('Buildings') if l!='Buildings']
    fronts= loc.layer_names('Front') + loc.layer_names('AlwaysFront')

    for j in range(th):
        for i in range(tw):
            k = j*tw+i; x,y = tx+i, ty+j
            base = [isw[k]]*256
            keep = None
            labelled = False
            if not isw[k]:
                # shore tile: label-first (Back), fallback colour if animated etc. (skip anim path)
                for ln in backs:
                    c = loc.cell(ln,x,y)
                    if not c: continue
                    lb = label_bits(c[0], c[1])
                    if lb and sum(lb[1:])>0: base = lb[0][:]
                    break
            else:
                for ln in backs:
                    c = loc.cell(ln,x,y)
                    if not c: continue
                    lb = label_bits(c[0], c[1])
                    if lb and sum(lb[1:])>7: keep = lb[0][:]
                    break
            # overlays
            carve = [False]*256
            big = False
            for ln in blds + fronts:
                c = loc.cell(ln,x,y)
                if not c: continue
                px = tile_art(c[0], c[1])
                if px is None: continue
                sb, cnt = solid_bits(px)
                ol = label_bits(c[0], c[1])
                if ol is not None and sum(ol[1:])>0 and isw[k]:
                    labelled = True
                    olb = ol[0]
                    kk = keep[:] if keep else [True]*256
                    for p in range(256):
                        if sb[p] and not olb[p]: kk[p]=False
                    keep = kk
                    for p in range(256):
                        if sb[p] and not olb[p]: carve[p]=True
                else:
                    for p in range(256):
                        if sb[p]: carve[p]=True
                    if cnt>=230: big=True
            deck = (x,y) in deck_tiles
            struct_whole = (deck or (big and not labelled)) and (big or deck)
            pixel_carve  = labelled and not struct_whole and big
            for py in range(SUB):
                row=(j*SUB+py)*pw + i*SUB; ar=py*SUB
                for pxi in range(SUB):
                    v = base[ar+pxi]
                    e = v and not (keep and not keep[ar+pxi]) and not carve[ar+pxi]
                    m = v
                    if struct_whole: m=False
                    elif pixel_carve and carve[ar+pxi]: m=False
                    eff[row+pxi]=e; march[row+pxi]=m
    # Pass D: per-column run tops + short-run drop
    edge=[0]*(pw*ph)
    for x in range(pw):
        top=0; inrun=False
        for y in range(ph+1):
            p=y*pw+x
            if y<ph and march[p]:
                if not inrun: inrun=True; top=y
                edge[p]=top
            elif inrun:
                if y<ph and y-top<6:
                    for kk in range(top,y): march[kk*pw+x]=False
                inrun=False
    return eff, march, edge, pw, ph

def g_at(march, pw, ph, x, y):
    if x<0 or y<0 or x>=pw or y>=ph: return 0.0
    return 1.0 if march[y*pw+x] else 0.0

def render(loc, tx, ty, tw, th, out, deck_tiles=frozenset(), attrib_col=None):
    eff, march, edge, pw, ph = build_mask(loc, tx, ty, tw, th, deck_tiles)
    frame = art_img(loc, tx, ty, tw, th)
    src = frame.load()
    outim = frame.copy(); po = outim.load()
    rows_report = {}
    for y in range(ph):
        for x in range(pw):
            p=y*pw+x
            if not eff[p] and not march[p]: continue
            col = src[x,y]
            if not march[p]:
                # effect-only: sheen path with found=0
                sheen = tuple(int(c+(s-c)*0.12) for c,s in zip(col,SKY))
                amt = 0.71*1.0*1.0*min(1.0, (0.299*col[0]+0.587*col[1]+0.114*col[2])/255*3.2)*0.5
                po[x,y]=tuple(int(c+(r-c)*amt) for c,r in zip(col,sheen)); continue
            waterOff=(y-edge[p])/16.0/ (ph/16.0)   # tiles -> screen fraction of window height
            depth=waterOff
            depthT=(y-edge[p])/16.0
            sy = y - int(round((y-edge[p])*2.25)) - int(round(0.08*16))   # edgeV - depth*1.25 in px
            sy = max(0, min(ph-1, sy))
            refl = src[x, sy]
            srcW = sum(g_at(march,pw,ph,x,sy+d) for d in (-13,-6,0,6,13))/5.0
            fade = max(0.0, 1.0 - depth*0.5*(ph/16.0)/TPSY*16.0/ (ph/16.0))  # ≈1 here; window-local
            fade = max(0.0, 1.0 - (depthT/TPSY)*0.5)
            found = 1.0
            def sstep(a,b,v):
                t=max(0.0,min(1.0,(v-a)/(b-a))); return t*t*(3-2*t)
            toSky=max(sstep(5,9,depthT), srcW*sstep(2,4,depthT))
            mirror=tuple(int(r*m) for r,m in zip(refl,(0.66,0.76,0.92)))
            skyS=tuple(int(c+(s-c)*0.25) for c,s in zip(col,SKY))
            mirror=tuple(int(m+(s-m)*toSky) for m,s in zip(mirror,skyS))
            lum=(0.299*col[0]+0.587*col[1]+0.114*col[2])/255
            amt=0.71*1.0*fade*min(1.0,lum*3.2)*1.0
            po[x,y]=tuple(int(c+(m-c)*amt) for c,m in zip(col,mirror))
            if attrib_col is not None and x==attrib_col and y%4==0:
                rows_report[y]=(depthT, srcW, toSky, fade, sy)
    outim=outim.resize((pw*2,ph*2),Image.NEAREST)
    outim.save(out)
    return rows_report
