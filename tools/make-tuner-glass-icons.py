"""Make assets/tuner-glass-icons.png, the line icons the F6 menu's glass panel uses, from the icons of
the menu mock (rendered white on black by a browser into a JSON file: {"names", "sizes", "png" data URL}).

    make-tuner-glass-icons.py <icons.json>

The browser draws every icon once per size, one row of cells per size, largest first, each from the
vector shape at that size. The game picks the row nearest the size it draws at, so an icon is never
shrunk by more than a little: a 48 pixel icon squeezed to 20 by the graphics card came out jagged.
Here the brightness becomes the alpha of a white icon, so the game can tint it any colour, and the
order of the names and the sizes are printed for the code.
"""
import base64
import io
import json
import os
import sys

import numpy
from PIL import Image


def main():
    data = json.loads(open(sys.argv[1], encoding="utf-8").read())
    if isinstance(data, str):
        data = json.loads(data)
    raw = base64.b64decode(data["png"].split(",", 1)[1])
    strip = numpy.asarray(Image.open(io.BytesIO(raw)).convert("L"), dtype=numpy.uint8)
    alpha = strip
    white = numpy.full(alpha.shape, 255, dtype=numpy.uint8)
    icons = numpy.dstack([white, white, white, alpha])
    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "tuner-glass-icons.png")
    Image.fromarray(icons, "RGBA").save(target, optimize=True)
    print(f"wrote {os.path.abspath(target)}: {len(data['names'])} icons at {data['sizes']} px")
    print(", ".join(f'"{name}"' for name in data["names"]))


if __name__ == "__main__":
    main()
