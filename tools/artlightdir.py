"""Measure which side Stardew's art is painted as lit from.

The question this answers: SDV-Radiance sweeps its sun across the sky, so for half of every
day the cast shadow implies a light on the opposite side from the highlight the artist painted
into the sprite. Acquire's stated HD-2D rule is to aim the 3D light the same way the art is
painted. Before acting on that we need to know which way SDV's art actually is, and "pixel art
is lit from the upper left" is a convention, not a measurement.

Method, and why it is this one rather than "look at it":

For each sprite cell we take only the OPAQUE pixels and fit the luminance against the x
position inside the cell, then again against y. The sign of the slope is which side is
brighter. Averaging the per-cell slopes over thousands of cells turns a subjective judgement
into a number with a spread attached.

Two things are deliberately excluded:
  - cells with too few opaque pixels, or too little luminance variance, because a flat colour
    block has no lighting to read and would just add noise around zero.
  - the alpha channel is a hard threshold, not a blend: antialiased rims would drag the slope
    outward on both sides and cancel.

The Y axis is reported but should NOT be trusted as a lighting signal: everything in this art
is darker at its base because of ground contact and the object's own shadow, so top-brighter
is guaranteed regardless of where the light is. The X axis is the informative one, and it is
the one the sun-hemisphere decision depends on.
"""
import sys, os, glob
import numpy as np
from PIL import Image

SHEETS = os.path.expanduser(r"~\Documents\HF-Studio\sheets")

def cell_slopes(img, cw, ch):
    """Per-cell (x_slope, y_slope) in luminance units per cell-width, opaque pixels only."""
    a = np.asarray(img.convert("RGBA"), dtype=np.float32)
    h, w = a.shape[:2]
    lum = 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]
    op = a[..., 3] > 128
    out = []
    for cy in range(0, h - ch + 1, ch):
        for cx in range(0, w - cw + 1, cw):
            m = op[cy:cy + ch, cx:cx + cw]
            n = int(m.sum())
            if n < cw * ch * 0.35:            # mostly empty cell: nothing to read
                continue
            L = lum[cy:cy + ch, cx:cx + cw][m]
            if L.std() < 12.0:                # flat colour: no shading painted in
                continue
            ys, xs = np.nonzero(m)
            # normalise position to [-0.5, +0.5] so the slope is per cell width
            xn = xs / (cw - 1) - 0.5
            yn = ys / (ch - 1) - 0.5
            Lc = L - L.mean()
            vx = (xn * xn).sum()
            vy = (yn * yn).sum()
            if vx < 1e-6 or vy < 1e-6:
                continue
            out.append(((xn * Lc).sum() / vx, (yn * Lc).sum() / vy))
    return out

def report(name, slopes):
    if not slopes:
        print(f"{name:<44} no usable cells")
        return None
    s = np.array(slopes)
    xs, ys = s[:, 0], s[:, 1]
    left = float((xs < 0).mean()) * 100.0
    print(f"{name:<44} cells {len(s):>6}   x-slope {xs.mean():+7.2f} (median {np.median(xs):+7.2f})"
          f"   left-brighter {left:5.1f}%   y-slope {ys.mean():+7.2f}")
    return xs

def main():
    pats = sys.argv[1:] or ["*_spring_outdoorsTileSheet.png", "Craftables*.png", "springobjects*.png"]
    cw = ch = 16
    allx = []
    for pat in pats:
        for p in sorted(glob.glob(os.path.join(SHEETS, pat)))[:12]:
            try:
                img = Image.open(p)
            except Exception as e:
                print(f"{os.path.basename(p)}: {e}")
                continue
            xs = report(os.path.basename(p), cell_slopes(img, cw, ch))
            if xs is not None:
                allx.append(xs)
    if allx:
        a = np.concatenate(allx)
        print()
        print(f"POOLED  cells {len(a)}   mean x-slope {a.mean():+.2f}   median {np.median(a):+.2f}   "
              f"left-brighter {float((a < 0).mean())*100:.1f}%")
        print("negative x-slope = brighter on the LEFT = art painted as lit from the left")

if __name__ == "__main__":
    main()
