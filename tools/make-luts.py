"""Generate the colour lookup tables shipped in assets/luts/.

A LUT is a 32x32x32 cube unrolled into a 1024x32 strip: 32 slices side by side, one per blue
level, each slice 32x32 with red across and green down. That layout is what every LUT tool
exports, so an artist can drop their own file in beside these.

    make-luts.py [outdir]

IDENTITY IS NOT DECORATION. identity.png maps every colour to itself, so applying it at full
strength must leave the frame byte-for-byte unchanged. That is the only cheap proof that the
shader's trilinear sampling is right - a half-texel slip shows up as a colour shift no eye would
catch but the pixel harness reports exactly.

The others are defined by arithmetic, not by an artist's taste: they are starting points to be
replaced, and they say so rather than pretending to be a graded look.
"""
import os, sys

try:
    from PIL import Image
except ImportError:
    raise SystemExit("needs Pillow (anaconda env)")

N = 32                      # cube edge
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))), "assets", "luts")


def build(fn):
    """fn(r, g, b) -> (r, g, b), each 0..1. Laid out as the strip described above."""
    img = Image.new("RGB", (N * N, N))
    px = img.load()
    for b in range(N):
        for g in range(N):
            for r in range(N):
                cr, cg, cb = fn(r / (N - 1), g / (N - 1), b / (N - 1))
                px[b * N + r, g] = (int(round(min(max(cr, 0.0), 1.0) * 255)),
                                    int(round(min(max(cg, 0.0), 1.0) * 255)),
                                    int(round(min(max(cb, 0.0), 1.0) * 255)))
    return img


def identity(r, g, b):
    """Changes nothing. Applied at full strength the frame must come back byte for byte, which is
    the only cheap proof that the shader's trilinear sampling is right."""
    return r, g, b


# --- helpers shared by the looks below -----------------------------------------------------
LUMA = (0.2126, 0.7152, 0.0722)


def lum(r, g, b):
    return LUMA[0] * r + LUMA[1] * g + LUMA[2] * b


def protect_black(fn):
    """Every look fades back to identity through the deepest shadows.

    MEASURED: 12% of all pixels are pure black, and 70% of an indoor night frame is. A lifted toe
    - the first thing every film LUT does - would turn most of a night interior grey, which is the
    exact symptom two players have already reported as "indoor GI goes grey-blue". So no look here
    is allowed to touch the bottom of the range.
    """
    def wrapped(r, g, b):
        out = fn(r, g, b)
        k = min(lum(r, g, b) / 0.10, 1.0)          # full effect only above luminance 0.10
        k = k * k * (3.0 - 2.0 * k)
        return tuple(c0 + (c1 - c0) * k for c0, c1 in zip((r, g, b), out))
    return wrapped


def mix(c, target, amount):
    return c + (target - c) * amount


@protect_black
def warm_film(r, g, b):
    """Warmth by moving the BLUE/GREEN mass, not by lifting red.

    MEASURED: blue+cyan+green is 78.6% of the saturated daytime picture and red+orange+yellow only
    19.1%, so pushing red touches a fifth of the frame while pulling blue touches four fifths.
    """
    b = mix(b, b * 0.90, 0.85)                     # take the blue down
    g = mix(g, g * 0.98 + 0.012, 0.8)              # green a hair toward yellow
    r = r * 1.02
    return r, g, b


@protect_black
def cool_night(r, g, b):
    """Cool and a little drained - but the reds are protected.

    MEASURED: 65.4% of saturated NIGHT pixels are red or orange, which is lamplight, and lamplight
    is what this mod exists to show. The textbook Purkinje shift drains exactly that.
    """
    l = lum(r, g, b)
    warmth = max(r - max(g, b), 0.0)               # how lamp-like this pixel is
    keep = min(warmth * 4.0, 1.0)                  # 1 = fully protected
    drain = 0.45 * (1.0 - keep)
    r2, g2, b2 = mix(r, l, drain), mix(g, l, drain), mix(b, l, drain)
    return r2 * 0.96, g2 * 0.99, min(b2 * 1.08, 1.0)


@protect_black
def verdant(r, g, b):
    """Greens and cyans get their own lift; the heavy blue mass comes down so they can be seen.
    Aimed at spring and summer, where green is 19% and cyan 17.5% of the colour."""
    l = lum(r, g, b)
    greenish = max(g - max(r, b), 0.0)
    k = min(greenish * 3.0, 1.0)
    g = g + (g - l) * 0.35 * k
    b = b * 0.93
    return r, min(g, 1.0), b


@protect_black
def autumn_gold(r, g, b):
    """Pull the greens toward gold, leave everything else. The fall look without waiting for fall."""
    greenish = max(g - max(r, b), 0.0)
    k = min(greenish * 3.5, 1.0)
    r = r + (0.55 - r) * 0.35 * k
    g = g * (1.0 - 0.10 * k)
    b = b * (1.0 - 0.35 * k)
    return min(r, 1.0), g, b


@protect_black
def moonlit(r, g, b):
    """Silver-blue with the contrast put into the MIDTONES rather than the highlights.

    MEASURED: 99% of the picture sits below 237/255 and only 0.9% of pixels go above 240, so a
    filmic shoulder has almost nothing to act on. The midtones are where the picture lives.
    """
    l = lum(r, g, b)
    warmth = max(r - max(g, b), 0.0)
    keep = min(warmth * 4.0, 1.0)
    drain = 0.35 * (1.0 - keep)
    r, g, b = mix(r, l, drain), mix(g, l, drain), mix(b, l, drain)
    # S-curve on midtones only, identity at both ends
    def mid(c):
        t = min(max((c - 0.10) / 0.70, 0.0), 1.0)
        s = t * t * (3.0 - 2.0 * t)
        return c + (0.10 + s * 0.70 - c) * 0.45
    return mid(r) * 0.95, mid(g) * 0.98, min(mid(b) * 1.06, 1.0)


@protect_black
def washed_linen(r, g, b):
    """Soft and low-contrast, for people who find the mod's look too strong.

    MEASURED: mean saturation is 0.531 - pixel art is far more saturated than photography, so the
    safe direction is DOWN. Nothing here adds saturation anywhere.
    """
    l = lum(r, g, b)
    r, g, b = mix(r, l, 0.22), mix(g, l, 0.22), mix(b, l, 0.22)
    def soften(c):
        return c * 0.92 + 0.02 * min(c / 0.15, 1.0)   # gentle, and zero at zero
    return soften(r), soften(g), soften(b) * 1.01


def main():
    os.makedirs(OUT, exist_ok=True)
    looks = (("identity", identity), ("warm-film", warm_film), ("cool-night", cool_night),
             ("verdant", verdant), ("autumn-gold", autumn_gold), ("moonlit", moonlit),
             ("washed-linen", washed_linen))
    for name, fn in looks:
        path = os.path.join(OUT, name + ".png")
        build(fn).save(path)
        z = fn(0.0, 0.0, 0.0)
        black = "black stays black" if max(z) < 0.002 else f"BLACK LIFTED TO {z} - fix it"
        print(f"{name+'.png':16} {os.path.getsize(path):>7} bytes   {black}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
