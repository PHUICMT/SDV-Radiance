"""Generate assets/caustics.png, the tileable caustic net the water shader lays on shallow beds.

    make-caustics.py [outfile]

The pattern is a Voronoi ridge: scatter one feature point per lattice cell, and for every pixel
take the distance to the nearest and second-nearest point. Where those two are equal the pixel
sits on a border between cells, and the set of all such borders is a net of thin walls - which is
exactly what focused light on a pool floor looks like. `1 - (F2 - F1)` makes the walls bright,
a second, finer octave breaks up the big cells' sameness. The web is left BROAD on purpose:
the shader multiplies two scrolling copies and sharpens the product, and a product of two
pre-sharpened nets is a field of dots at the crossings, not a net - measured in game as
'nothing visible at all'.

Two things are load-bearing rather than taste:

- The lattice wraps modulo its own size in both axes, so the texture tiles perfectly. The shader
  samples it in world space with wrap addressing; a seam would march across every lake in the
  game in a straight line.
- The output is grayscale written as equal RGB. The shader reads only .r, but an equal-channel
  file survives any loader that expands L to RGB differently.

Determinism: the RNG is seeded with a constant, so the shipped texture is reproducible from this
script alone. Regenerating it is never a diff unless the recipe itself changed.
"""
import math
import os
import random
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required (the anaconda env has it): D:/Program/anaconda3/envs/ml/python.exe")

SIZE = 256


def feature_points(cells: int, rng: random.Random) -> list[list[tuple[float, float]]]:
    """One jittered feature point per lattice cell, indexed [cy][cx], in pixel coordinates."""
    points = []
    for cy in range(cells):
        row = []
        for cx in range(cells):
            row.append(((cx + rng.random()) * SIZE / cells,
                        (cy + rng.random()) * SIZE / cells))
        points.append(row)
    return points


def ridge(pixel_x: float, pixel_y: float, cells: int, points: list[list[tuple[float, float]]]) -> float:
    """1 at a Voronoi cell border, falling to 0 at the cell's heart. Toroidal metric."""
    cell = SIZE / cells
    cell_x, cell_y = int(pixel_x / cell), int(pixel_y / cell)
    nearest = second_nearest = float("inf")
    for offset_y in (-1, 0, 1):
        for offset_x in (-1, 0, 1):
            feature_x, feature_y = points[(cell_y + offset_y) % cells][(cell_x + offset_x) % cells]
            delta_x = feature_x - pixel_x
            delta_y = feature_y - pixel_y
            # Wrap the distance itself: the nearest image of the point may be across the edge.
            delta_x = (delta_x + SIZE / 2) % SIZE - SIZE / 2
            delta_y = (delta_y + SIZE / 2) % SIZE - SIZE / 2
            distance = math.hypot(delta_x, delta_y)
            if distance < nearest:
                nearest, second_nearest = distance, nearest
            elif distance < second_nearest:
                second_nearest = distance
    return 1.0 - min(1.0, (second_nearest - nearest) / (cell * 0.5))


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(__file__), "..", "assets", "caustics.png")
    rng = random.Random(49397)          # the mod's Nexus id: constant, arbitrary, memorable
    coarse = feature_points(8, rng)
    fine = feature_points(16, rng)

    values = []
    for pixel_y in range(SIZE):
        for pixel_x in range(SIZE):
            value = ridge(pixel_x + 0.5, pixel_y + 0.5, 8, coarse)
            value += 0.35 * ridge(pixel_x + 0.5, pixel_y + 0.5, 16, fine)
            values.append(max(0.0, value / 1.35))

    # Normalize to the full byte range so CausticAmt is the only brightness dial.
    lowest, highest = min(values), max(values)
    span = (highest - lowest) or 1.0
    img = Image.new("RGB", (SIZE, SIZE))
    img.putdata([(byte, byte, byte) for value in values for byte in (round((value - lowest) / span * 255),)])
    img.save(out)
    print(f"wrote {out} ({SIZE}x{SIZE}), range {lowest:.3f}..{highest:.3f} before normalize")


if __name__ == "__main__":
    main()
