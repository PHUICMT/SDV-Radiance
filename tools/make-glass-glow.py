"""Generate assets/glass-glow.png, the soft round glow a lamp leaves in a window after dark.

    make-glass-glow.py [outfile]

At night a street is lit by lamps, and every pane of glass facing it carries them: a shop front
opposite a lamp post holds a warm blot of it, softer and wider than the lamp itself because glass
returns light without returning detail. The mod knows where every light in the location is and
what colour it burns; this file is the shape it draws them with.

The profile is a squared falloff with a small flat core - bright in the middle, gone by the edge,
and nothing at all outside the circle, so a glow never leaves a square edge on the glass when it
is cut off by the pane. Nothing about it is directional: it is tinted and sized in code from the
light's own colour and radius.

Two things are load-bearing rather than taste:

- The falloff reaches EXACTLY zero at the texture's edge. A profile that still had a few levels
  left at the last texel drew a visible ring where the sprite ended.
- The file is written PREMULTIPLIED (every channel equal to the intensity), because the world
  batch this is drawn into blends premultiplied alpha and loads it straight from the PNG with no
  conversion.

Determinism: no randomness at all, so the shipped texture is reproducible from this script alone.
"""
import math
import os
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required (the anaconda env has it): D:/Program/anaconda3/envs/ml/python.exe")

SIZE = 64
CORE_RADIUS = 0.18


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "glass-glow.png")
    image = Image.new("RGBA", (SIZE, SIZE))
    pixels = image.load()
    centre = (SIZE - 1) / 2.0
    for y in range(SIZE):
        for x in range(SIZE):
            distance = math.hypot(x - centre, y - centre) / centre
            if distance >= 1.0:
                intensity = 0.0
            elif distance <= CORE_RADIUS:
                intensity = 1.0
            else:
                edge = 1.0 - (distance - CORE_RADIUS) / (1.0 - CORE_RADIUS)
                intensity = edge * edge
            level = int(round(255 * intensity))
            pixels[x, y] = (level, level, level, level)
    image.save(out)
    print(f"wrote {out} ({SIZE}x{SIZE}, premultiplied)")


if __name__ == "__main__":
    main()
