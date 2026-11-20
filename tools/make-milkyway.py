"""Generate assets/milkyway.png, the Milky Way's glow that the water shader shows in its reflected sky.

    make-milkyway.py [outfile]

Why a texture: the water shader is at the ps_3_0 limit of constant registers (224), and the band
written in the shader with its own noise needed some forty literal constants more than were left.
A texture costs one sampler and a handful of constants. The fine star dust stays in the shader,
because a star has to be a few screen pixels wide and a texture fine enough for that would be
megabytes.

The shader samples this in world space with wrap addressing, so the texture has to tile. The band
runs along (2, 1), 26.6 degrees off the horizontal, and repeats every 40 tiles along its normal
(-1, 2) / sqrt(5). On a square of side 40 * sqrt(5) tiles that band meets itself at every edge,
which is the whole reason for the odd angle and the odd size. Every noise term below is a sum of
sines with whole-number frequencies across the square, so it wraps too.

The band is wide on purpose: the sky only shows where there is water, and a band a few tiles
across lands on the water of a given screen less often than not (first try, 18 Oct 2026: the
core sat in one corner of the lake and the rest of the water had only the faint halo).

What it looks like follows what an eye sees from a dark site, not a long-exposure photograph:
grey-white, only a breath of blue at the edges and of cream where the star clouds are thick, uneven
in width and brightness, split along its length by a dark rift (the Great Rift is the easiest dark
structure to see without a telescope), with one brighter, wider stretch for the summer core.

What it holds: the haze as plain grey, already scaled by how bright it is there. Its colour is
not in the file: the shader tints the faint edges and the bright core with two colours the CPU
works out from the player's Milky Way colour dial, from naked-eye grey to a photograph's warm core
and violet arms, so one texture serves both. Alpha is opaque. The shader also reads green as the
band's density, to put more stars inside the band than outside it.

Determinism: the RNG is seeded with a constant, so the shipped texture is reproducible.
"""
import math
import os
import random
import sys

try:
    import numpy
    from PIL import Image
except ImportError:
    sys.exit("numpy and Pillow are required (the anaconda env has them): D:/Program/anaconda3/envs/ml/python.exe")

SIZE = 256
BAND_PERIOD_TILES = 40.0
SIDE_TILES = BAND_PERIOD_TILES * math.sqrt(5.0)


def periodic_noise(u, v, rng, lowest, highest, terms):
    """A smooth field on the unit square that wraps in both axes, roughly 0 to 1."""
    total = numpy.zeros_like(u)
    weight_sum = 0.0
    for _ in range(terms):
        frequency_u = rng.randint(-highest, highest)
        frequency_v = rng.randint(-highest, highest)
        length = math.hypot(frequency_u, frequency_v)
        if length < lowest:
            frequency_u, frequency_v = lowest, 0
            length = lowest
        weight = 1.0 / length
        total += weight * numpy.sin(2.0 * math.pi * (frequency_u * u + frequency_v * v) + rng.random() * 2.0 * math.pi)
        weight_sum += weight
    return 0.5 + 0.5 * total / weight_sum * 1.8


def main():
    output = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "assets", "milkyway.png")
    rng = random.Random(0x4D57)
    pixel = (numpy.arange(SIZE) + 0.5) / SIZE
    u, v = numpy.meshgrid(pixel, pixel)

    # How far across the band each pixel sits, in tiles. (-u + 2v) runs 0..1 once per band
    # period over the square, so its fraction is the position within one band.
    meander_tiles = 8.0 * (periodic_noise(u, v, rng, 1, 4, 8) - 0.5)
    across = -u + 2.0 * v + meander_tiles / BAND_PERIOD_TILES
    from_centre = (numpy.mod(across, 1.0) - 0.5) * BAND_PERIOD_TILES

    core = numpy.exp(-from_centre ** 2 / (2.0 * 4.5 ** 2))
    halo = numpy.exp(-from_centre ** 2 / (2.0 * 8.0 ** 2))
    star_clouds = (0.30 + 1.10 * periodic_noise(u, v, rng, 3, 9, 14) ** 2) * (0.60 + 0.70 * periodic_noise(u, v, rng, 8, 20, 18))
    # Fine knots a few tiles across. The water shows only a stretch of the band at a time, ten or
    # fifteen tiles of it, and without structure at that size the stretch reads as a glow.
    star_clouds *= 0.35 + 1.1 * periodic_noise(u, v, rng, 18, 40, 40) ** 1.5
    star_clouds = numpy.clip(star_clouds, 0.2, 1.6)

    # The dark lane: just off the centre line, wandering, and broken along its length.
    lane_centre = 1.4 + 2.0 * (periodic_noise(u, v, rng, 2, 5, 8) - 0.5)
    lane = numpy.exp(-(from_centre - lane_centre) ** 2 / (2.0 * 1.6 ** 2))
    lane *= numpy.clip((periodic_noise(u, v, rng, 3, 8, 10) - 0.15) / 0.35, 0.0, 1.0)
    # The bulge: one stretch of the band per square is wider and brighter, the way the summer
    # core stands out of the rest of it.
    bulge = numpy.clip((periodic_noise(u, v, rng, 1, 2, 4) - 0.45) / 0.4, 0.0, 1.0) ** 2

    # Dark clouds of dust that eat whole stretches of the band, the reason the real one
    # looks torn rather than painted.
    dark_patches = numpy.clip((periodic_noise(u, v, rng, 4, 12, 16) - 0.55) / 0.25, 0.0, 1.0)
    haze = (core * star_clouds * (1.0 - 0.95 * lane) * 0.75 + halo * 0.25) * (1.0 - 0.4 * dark_patches) * (1.0 + 0.5 * bulge)
    warmth = numpy.clip(core * star_clouds * 0.5, 0.0, 1.0)[..., None]
    edge_colour = numpy.array([1.0, 1.0, 1.0])
    core_colour = numpy.array([1.0, 1.0, 1.0])
    colour = edge_colour * (1.0 - warmth) + core_colour * warmth
    rgb = numpy.clip(colour * haze[..., None], 0.0, 1.0)

    image = numpy.dstack([rgb * 255.0, numpy.full((SIZE, SIZE), 255.0)]).round().astype(numpy.uint8)
    Image.fromarray(image, "RGBA").save(output, optimize=True)
    print(f"wrote {os.path.abspath(output)} ({SIZE}x{SIZE}, {SIDE_TILES:.2f} tiles a side)")


if __name__ == "__main__":
    main()
