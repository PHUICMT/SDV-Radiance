"""Make the pictures the F6 menu's help bar shows: assets/tuner-pictures/<name>-a.png (the game alone)
and <name>-b.png (the same moment with the effect on).

    make-tuner-pictures.py <folder of shots> [names to leave out ...]

The shots are game-alone / game-plus-effect pairs taken in game for the menu mock (560x315 JPEG,
"<name>-a.jpg" and "<name>-b.jpg"). They are shrunk to the size the help bar draws them at, so the
mod carries no more than it shows, and saved as PNG because that is what the game reads everywhere.
"""
import os
import sys

from PIL import Image

WIDTH, HEIGHT = 320, 180
# A palette rather than full colour: the pictures are the game's pixel art, and 192 colours keep
# them indistinguishable at this size while each file is a third of the full-colour one.
COLOURS = 192


def main():
    source = sys.argv[1]
    left_out = set(sys.argv[2:])
    target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "tuner-pictures")
    os.makedirs(target, exist_ok=True)
    names = sorted({name[:-6] for name in os.listdir(source) if name.endswith("-a.jpg")})
    written = 0
    for name in names:
        if name in left_out or not os.path.exists(os.path.join(source, name + "-b.jpg")):
            continue
        for half in ("a", "b"):
            picture = Image.open(os.path.join(source, f"{name}-{half}.jpg")).convert("RGB")
            picture = picture.resize((WIDTH, HEIGHT), Image.LANCZOS)
            picture = picture.quantize(colors=COLOURS, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG)
            picture.save(os.path.join(target, f"{name}-{half}.png"), optimize=True)
            written += 1
    total = sum(os.path.getsize(os.path.join(target, file)) for file in os.listdir(target))
    print(f"wrote {written} pictures to {os.path.abspath(target)}, {total / 1024:.0f} KB in all")


if __name__ == "__main__":
    main()
