"""Make assets/tuner-font.png and assets/tuner-font.json: Nunito SemiBold as a glyph atlas, the
typeface of the F6 menu mock, for the glass panel's Latin text.

    make-tuner-font.py <Nunito[wght].ttf>

The game draws text only from its own sprite fonts, so a typeface of our own has to arrive as a
picture of every glyph and the numbers to place them: where each one sits in the atlas, how far it
hangs from the baseline and how far the pen moves after it. Only printable ASCII and a few marks the
menu uses are included; any string with a character outside them is drawn in the game's own font,
which is how Thai and Chinese keep working.

Nunito is under the SIL Open Font License 1.1 (assets/tuner-font-OFL.txt).
"""
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

SIZE = 40
EXTRA = "•…’“”°×"
PAD = 2


def main():
    font = ImageFont.truetype(sys.argv[1], SIZE)
    font.set_variation_by_name("SemiBold")
    characters = [chr(code) for code in range(32, 127)] + list(EXTRA)
    ascent, descent = font.getmetrics()
    line_height = ascent + descent
    columns = 16
    cell_width, cell_height = SIZE + PAD * 2, line_height + PAD * 2
    rows = (len(characters) + columns - 1) // columns
    atlas = Image.new("L", (columns * cell_width, rows * cell_height), 0)
    draw = ImageDraw.Draw(atlas)
    glyphs = {}
    for index, character in enumerate(characters):
        column, row = index % columns, index // columns
        left, top, right, bottom = font.getbbox(character, anchor="ls")
        advance = font.getlength(character)
        x = column * cell_width + PAD
        y = row * cell_height + PAD
        if right > left and bottom > top:
            draw.text((x - left, y - top), character, font=font, fill=255, anchor="ls")
        glyphs[str(ord(character))] = [x, y, max(0, right - left), max(0, bottom - top), left, top, round(advance, 2)]
    folder = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets")
    white = Image.new("L", atlas.size, 255)
    Image.merge("RGBA", (white, white, white, atlas)).save(os.path.join(folder, "tuner-font.png"), optimize=True)
    with open(os.path.join(folder, "tuner-font.json"), "w", encoding="utf-8") as file:
        json.dump({"size": SIZE, "ascent": ascent, "lineHeight": line_height, "glyphs": glyphs}, file, separators=(",", ":"))
    print(f"wrote {len(glyphs)} glyphs, line height {line_height}, atlas {atlas.size}")


if __name__ == "__main__":
    main()
