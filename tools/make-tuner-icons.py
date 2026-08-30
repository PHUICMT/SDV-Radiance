"""Generate assets/tuner-icons.png - one 16x16 icon per tuner tab.

Hand-placed pixels rather than drawing primitives: at 16x16 every pixel is a design
decision, and a circle routine picks them badly. Each icon is an ASCII grid, so the art
stays editable by anyone who can count squares.

  .  transparent      o  outline (dark, reads against the cream menu)
  1  main fill        2  shade        3  highlight / accent

Run: python tools/make-tuner-icons.py
"""
import os
from PIL import Image

SIZE = 16
OUTLINE = (54, 33, 22, 255)

# Per-icon palette: (fill, shade, accent)
ICONS = []


def icon(name, palette, rows):
    assert len(rows) == SIZE, f"{name}: {len(rows)} rows"
    for r in rows:
        assert len(r) == SIZE, f"{name}: row '{r}' is {len(r)} wide"
    ICONS.append((name, palette, rows))


# 1. Looks - a sparkle. The "make it pretty" tab.
icon("looks", ((255, 216, 102), (232, 170, 60), (255, 247, 214)), [
    "................",
    ".......oo.......",
    "......o11o......",
    "......o11o......",
    "......o11o......",
    ".....o1331o.....",
    "..ooo113311ooo..",
    ".o111133331111o.",
    ".o111133331111o.",
    "..ooo112211ooo..",
    ".....o1221o.....",
    "......o22o......",
    "......o22o......",
    "......o22o......",
    ".......oo.......",
    "................",
])

# 2. Performance - a gauge with the needle swung high.
icon("perf", ((214, 220, 228), (150, 160, 172), (208, 90, 70)), [
    "................",
    ".....oooooo.....",
    "...oo111111oo...",
    "..o1111111111o..",
    ".o111111111331o.",
    ".o111111113311o.",
    "o11111111331111o",
    "o11111113311111o",
    "o11111222111111o",
    "o11111222111111o",
    ".o111111111111o.",
    ".o111111111111o.",
    "..o1111111111o..",
    "...oo111111oo...",
    ".....oooooo.....",
    "................",
])

# 3. Colour grade - overlapping colour swatches.
icon("colorgrade", ((208, 90, 70), (86, 150, 200), (240, 200, 80)), [
    "................",
    "................",
    "...oooooo.......",
    "..o111111o......",
    "..o111111o......",
    "..o1111ooooooo..",
    "..o1111o333333o.",
    "..o1111o333333o.",
    "..ooooo3333333o.",
    "....o2222o3333o.",
    "...o22222oooooo.",
    "...o222222o.....",
    "...o222222o.....",
    "...o222222o.....",
    "....oooooo......",
    "................",
])

# 4. Bloom - a sun bleeding light outwards.
icon("bloom", ((255, 224, 130), (240, 180, 60), (255, 250, 225)), [
    "................",
    "...3...3...3....",
    "....3..3..3.....",
    "......oooo......",
    "....oo3333oo....",
    "...o13333331o...",
    ".3.o13333331o.3.",
    "3..o13333331o..3",
    "3..o13333331o..3",
    ".3.o11333311o.3.",
    "...o11122111o...",
    "....oo2222oo....",
    "......oooo......",
    "....3..3..3.....",
    "...3...3...3....",
    "................",
])

# 5. Lens - an aperture with a flare highlight.
icon("lens", ((120, 132, 150), (70, 80, 96), (255, 255, 255)), [
    "................",
    ".....oooooo.....",
    "...oo111111oo...",
    "..o113333311o...",
    ".o1133333331o1o.",
    ".o113ooooo311oo.",
    "o1133o222o3311o.",
    "o113o22222o311o.",
    "o113o22222o311o.",
    "o1133o222o3311o.",
    ".o113ooooo3311o.",
    ".o11333333311o..",
    "..o1133333311o..",
    "...oo111111oo...",
    ".....oooooo.....",
    "................",
])

# 6. Smoothing - a pixel staircase with the smoothed diagonal skimming its corners,
# which is literally what the Scale2x tab does to the art.
icon("smoothing", ((152, 200, 132), (104, 152, 92), (86, 150, 200)), [
    "................",
    "..ooooooo.......",
    "..o111113o......",
    "..o11111o3......",
    "..o11111o.3.....",
    "..o11111oooo....",
    "..o11111111o3...",
    "..o11111111o.3..",
    "..o11111111oooo.",
    "..o11111111111o.",
    "..o11111111111o.",
    "..o11222222211o.",
    "..ooooooooooooo.",
    "................",
    "................",
    "................",
])

# 7. Lighting - a hanging lantern with a pool of light.
icon("lighting", ((255, 226, 150), (226, 170, 70), (120, 84, 50)), [
    ".......oo.......",
    ".......33.......",
    "......o33o......",
    ".....o3333o.....",
    "....oo3333oo....",
    "....o111111o....",
    "...o11111111o...",
    "...o11111111o...",
    "...o11111111o...",
    "...o11111111o...",
    "....o111111o....",
    "....oo2222oo....",
    ".....o3333o.....",
    "......oooo......",
    "...111....111...",
    "..11..........11",
])

# 7. Windows - a four-light window in a frame, one pane catching the light.
icon("windows", ((150, 200, 236), (96, 150, 200), (228, 244, 255)), [
    "................",
    "..oooooooooooo..",
    "..o2222222222o..",
    "..o2o11113111o..",
    "..o2o11131111o..",
    "..o2o11311111o..",
    "..o2o13111111o..",
    "..o2o11111111o..",
    "..o2ooooooooooo.",
    "..o2o11111111o..",
    "..o2o11111111o..",
    "..o2o11111111o..",
    "..o2o11111111o..",
    "..o2o11111111o..",
    "..oooooooooooo..",
    ".oooooooooooooo.",
])

# 8. Shadows - a figure and the shadow it throws.
icon("shadows", ((120, 84, 50), (70, 48, 30), (60, 60, 70)), [
    "................",
    "......oooo......",
    ".....o1111o.....",
    ".....o1111o.....",
    ".....o1111o.....",
    "......o11o......",
    "....oo1111oo....",
    "...o11111111o...",
    "...o11111111o...",
    "...o11111111o...",
    "....o111111o....",
    ".....o11o1o.....",
    ".....o11o1o.....",
    ".....oooooo.....",
    "...3333333333...",
    "..333333333333..",
])

# 9. God rays - beams fanning out of a light.
# Beams need a saturated gold, not the pale core colour: the menu behind them is cream,
# and the first pass rendered light-on-light where the icon needed to read at a glance.
icon("godrays", ((250, 214, 110), (226, 166, 52), (255, 248, 214)), [
    "................",
    "....oooooooo....",
    "...o33333333o...",
    "...o33333333o...",
    "...o33333333o...",
    "....oooooooo....",
    "...o11o..o11o...",
    "...o11o..o11o...",
    "..o111o..o111o..",
    "..o111o..o111o..",
    ".o1111o..o1111o.",
    ".o1111o..o1111o.",
    "o11111o..o11111o",
    "o11111o..o11111o",
    "ooooooo..ooooooo",
    "................",
])

# 10. Water - a droplet over ripples.
icon("water", ((90, 170, 220), (52, 120, 180), (200, 240, 255)), [
    "................",
    ".......oo.......",
    "......o11o......",
    "......o11o......",
    ".....o1111o.....",
    ".....o1331o.....",
    "....o113311o....",
    "....o113311o....",
    "...o11133111o...",
    "..o1113311111o..",
    "..o1111111111o..",
    "..o1111111112o..",
    "..o1111122222o..",
    "...o11222222o...",
    "....oooooooo....",
    "................",
])

# 11. Cloud shadows - a cloud and the patch it darkens.
icon("cloudshadow", ((248, 248, 252), (196, 202, 214), (110, 116, 130)), [
    "................",
    "......oooo......",
    "....oo1111oo....",
    "..oo11111111oo..",
    ".o1111111111111o",
    "o111111111111111",
    "o111111111111111",
    ".o2222222222222o",
    "..oo22222222oo..",
    "....oooooooo....",
    "................",
    "....333333333...",
    "..33333333333333",
    "...3333333333...",
    ".....333333.....",
    "................",
])

# 12. Fog - drifting bands.
icon("fog", ((226, 232, 240), (176, 186, 200), (140, 150, 166)), [
    "................",
    "................",
    "..ooooooooo.....",
    ".o111111111o....",
    "..ooooooooo.....",
    "................",
    "....ooooooooo...",
    "...o222222222o..",
    "....ooooooooo...",
    "................",
    "..ooooooooo.....",
    ".o333333333o....",
    "..ooooooooo.....",
    "................",
    "....oooooo......",
    "................",
])

# 13. Weather - a rain cloud with wind-slanted streaks falling out of it.
icon("weather", ((222, 230, 240), (168, 180, 196), (110, 170, 230)), [
    "................",
    ".....ooooo......",
    "....o11111o.....",
    "..oo1111111oo...",
    ".o11111111111o..",
    ".o11111111111o..",
    "o1111111111111o.",
    "o1122111221111o.",
    ".oo111111111oo..",
    "...ooooooooo....",
    "....3...3...3...",
    "...3...3...3....",
    "..3...3...3.....",
    ".....3...3...3..",
    "....3...3...3...",
    "................",
])

# 14. Particles - blossom and specks drifting through the frame.
icon("particles", ((255, 196, 214), (214, 146, 172), (255, 240, 246)), [
    "................",
    "........oo......",
    "...oo...22......",
    "..o11o..........",
    "..o13o..........",
    "...oo......oo...",
    "..........o11o..",
    "..........o13o..",
    ".oo........oo...",
    ".22.............",
    "......oo........",
    ".....o11o.......",
    ".....o13o....oo.",
    "......oo.....22.",
    "................",
    "................",
])


# 14. Camera - a camera body with a lens.
icon("camera", ((120, 132, 150), (70, 80, 96), (160, 200, 230)), [
    "................",
    ".....oooo.......",
    "....o1111o......",
    ".ooooooooooooo..",
    ".o11111111111o..",
    ".o111oooo1111o..",
    ".o11o2222o111o..",
    ".o11o2332o111o..",
    ".o11o2332o111o..",
    ".o11o2222o111o..",
    ".o111oooo1111o..",
    ".o11111111111o..",
    ".ooooooooooooo..",
    "................",
    "................",
    "................",
])

# 15. Diagnostics - a wrench.
icon("debug", ((186, 194, 206), (130, 140, 155), (90, 98, 112)), [
    # Standing upright, not on the diagonal: a 45-degree shaft has to climb one pixel per
    # row, and that staircase is what read as bent however the ends were drawn.
    "................",
    "....oo....oo....",
    "...o11o..o11o...",
    "...o11o..o11o...",
    "...o11oooo11o...",
    "...o11111111o...",
    "...o11111111o...",
    "....o111111o....",
    ".....o1111o.....",
    ".....o1111o.....",
    ".....o1111o.....",
    ".....o1111o.....",
    ".....o1122o.....",
    ".....o1122o.....",
    ".....o1222o.....",
    "......oooo......",
])


def main():
    sheet = Image.new("RGBA", (SIZE * len(ICONS), SIZE), (0, 0, 0, 0))
    for index, (name, palette, rows) in enumerate(ICONS):
        fill, shade, accent = palette
        colors = {"o": OUTLINE, "1": fill + (255,), "2": shade + (255,), "3": accent + (255,)}
        for y, row in enumerate(rows):
            for x, ch in enumerate(row):
                if ch == ".":
                    continue
                sheet.putpixel((index * SIZE + x, y), colors[ch])

    out = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "tuner-icons.png")
    sheet.save(out)
    print(f"wrote {out}  ({sheet.width}x{sheet.height}, {len(ICONS)} icons)")

    # Preview at 8x so the art can actually be looked at before it ships.
    preview = sheet.resize((sheet.width * 8, sheet.height * 8), Image.NEAREST)
    backdrop = Image.new("RGBA", preview.size, (247, 227, 187, 255))   # the menu's cream
    backdrop.alpha_composite(preview)
    prev_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tuner-icons-preview.png")
    backdrop.convert("RGB").save(prev_path)
    print(f"preview: {prev_path}")


if __name__ == "__main__":
    main()
