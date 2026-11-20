"""Make assets/tuner-font.png and assets/tuner-font.json: Nunito SemiBold as a glyph atlas, the
typeface of the F6 menu mock, for the glass panel's Latin text, and Sarabun SemiBold for its Thai.

    make-tuner-font.py <Nunito[wght].ttf> <Sarabun-SemiBold.ttf>
    make-tuner-font.py --thai-only <Sarabun-SemiBold.ttf>

--thai-only keeps the Latin half of the current atlas and its numbers as they are and remakes only
the Thai under it. A newer Pillow hints Nunito a pixel differently (86 of 102 glyphs moved when the
Thai was added on 27 Oct 2026), and the Latin text should not change because Thai was added.

The game draws text only from its own sprite fonts, so a typeface of our own has to arrive as a
picture of every glyph and the numbers to place them: where each one sits in the atlas, how far it
hangs from the baseline and how far the pen moves after it. Only printable ASCII and a few marks the
menu uses are included for Latin; any string with a character outside them is drawn in the game's
own font, which is how Chinese keeps working.

Thai cannot be placed one letter at a time: a tone mark sits higher over a vowel above the letter
than over the bare letter, the marks over a tall letter (po pla, fo fa, fo fan) move left, and yo
ying and tho than lose their tails over a vowel below. Sarabun carries those rules in its own tables,
so they are run here, once, by HarfBuzz, the shaper browsers use: every letter with every run of
marks it can carry is shaped and the result kept as a cluster, the glyphs to draw and where. The
game only looks a cluster up.
A Thai string with a cluster that is not in the table is drawn in the game's font, as before.

Needs uharfbuzz and freetype-py (pip). Nunito and Sarabun are under the SIL Open Font License 1.1
(assets/tuner-font-OFL.txt, assets/tuner-font-thai-OFL.txt).
"""
import json
import os
import sys

import freetype
import uharfbuzz as harfbuzz
from PIL import Image, ImageDraw, ImageFont

SIZE = 40
# Thai a size up from the Latin, so a Thai letter stands as tall as a Latin capital. At the Latin's
# own size it stood at the Latin x-height, which on the menu's small type read far smaller than the
# game's Thai font it replaces.
THAI_SIZE = 48
EXTRA = "•…’“”°×"
PAD = 2

THAI_BASES = [chr(code) for code in range(0x0E01, 0x0E2F)]
THAI_ALONE = [chr(code) for code in [0x0E2F, 0x0E30, 0x0E32, 0x0E33, *range(0x0E40, 0x0E47), *range(0x0E4F, 0x0E5C)]]
THAI_BELOW = ["\u0E38", "\u0E39", "\u0E3A"]
THAI_ABOVE = ["\u0E31", "\u0E34", "\u0E35", "\u0E36", "\u0E37", "\u0E47", "\u0E4D"]
THAI_TONES = ["\u0E48", "\u0E49", "\u0E4A", "\u0E4B", "\u0E4C"]
SARA_AM = "\u0E33"


def thai_mark_runs():
    """Every run of marks a Thai letter carries, in the order they are typed."""
    runs = [""]
    runs += THAI_BELOW + THAI_ABOVE + THAI_TONES
    runs += [above + tone for above in THAI_ABOVE for tone in THAI_TONES]
    runs += [below + tone for below in THAI_BELOW for tone in THAI_TONES]
    runs += [SARA_AM] + [tone + SARA_AM for tone in THAI_TONES]
    return runs


def latin(atlas_rows_out):
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
    atlas_rows_out.append(atlas)
    return ascent, line_height, glyphs


def thai(width, path):
    """Shape every Thai cluster with Sarabun; rasterise each glyph it uses once."""
    shaper_face = harfbuzz.Face(harfbuzz.Blob.from_file_path(path))
    shaper = harfbuzz.Font(shaper_face)
    units = shaper_face.upem
    to_pixels = THAI_SIZE / units
    dotted_circle = shaper.get_nominal_glyph(0x25CC)

    raster = freetype.Face(path)
    raster.set_pixel_sizes(0, THAI_SIZE)

    bitmaps = {}

    def bitmap(glyph_id):
        if glyph_id not in bitmaps:
            raster.load_glyph(glyph_id, freetype.FT_LOAD_RENDER | freetype.FT_LOAD_TARGET_LIGHT)
            slot = raster.glyph
            rows, columns, pitch = slot.bitmap.rows, slot.bitmap.width, slot.bitmap.pitch
            image = Image.new("L", (max(1, columns), max(1, rows)), 0)
            if rows and columns:
                data = bytes(slot.bitmap.buffer)
                image = Image.frombytes("L", (columns, rows), b"".join(data[r * pitch:r * pitch + columns] for r in range(rows)))
            bitmaps[glyph_id] = (image if rows and columns else None, slot.bitmap_left, slot.bitmap_top)
        return bitmaps[glyph_id]

    clusters, extents = {}, {}
    top, bottom = 0.0, 0.0
    texts = [base + run for base in THAI_BASES for run in thai_mark_runs()] + THAI_ALONE
    for text in texts:
        buffer = harfbuzz.Buffer()
        buffer.add_str(text)
        buffer.guess_segment_properties()
        harfbuzz.shape(shaper, buffer, {})
        ids = [info.codepoint for info in buffer.glyph_infos]
        if 0 in ids or (dotted_circle is not None and dotted_circle in ids):
            continue
        pen = 0.0
        placed = []
        top_here, bottom_here = 0.0, 0.0
        for info, position in zip(buffer.glyph_infos, buffer.glyph_positions):
            image, left, rise = bitmap(info.codepoint)
            if image is not None:
                x = (pen + position.x_offset) * to_pixels + left
                y = -position.y_offset * to_pixels - rise
                placed += [info.codepoint, round(x, 2), round(y, 2)]
                top_here = max(top_here, -y)
                bottom_here = max(bottom_here, y + image.height)
                top, bottom = max(top, top_here), max(bottom, bottom_here)
            pen += position.x_advance
        clusters[text] = [round(pen * to_pixels, 2)] + placed
        extents[text] = (top_here, bottom_here)

    # Shelf-pack the glyphs the clusters use under the Latin rows.
    used = sorted({glyph_id for numbers in clusters.values() for glyph_id in numbers[1::3]},
                  key=lambda glyph_id: -bitmaps[glyph_id][0].height)
    shelves, x, y, shelf_height, places = [], PAD, PAD, 0, {}
    for glyph_id in used:
        image = bitmaps[glyph_id][0]
        if x + image.width + PAD > width:
            x, y, shelf_height = PAD, y + shelf_height + PAD, 0
        places[glyph_id] = (x, y, image.width, image.height)
        x += image.width + PAD
        shelf_height = max(shelf_height, image.height)
    sheet = Image.new("L", (width, y + shelf_height + PAD), 0)
    for glyph_id, (gx, gy, _, _) in places.items():
        sheet.paste(bitmaps[glyph_id][0], (gx, gy))
    # The line a Thai string is set on: tall enough for a letter with a vowel above it (ko kai, sara ii) and one
    # below (ko kai, sara u). A tone mark over a vowel above (with mai ek) stands over the top of it, as Thai marks do
    # in any UI, rather than every line being tall enough for the rarest stack. Set on the extreme
    # (fo fan, sara ue, mai tho: 67 px over the baseline at 48) the letters sat in the bottom of their box and a centred
    # label read low in its button.
    line_top, line_bottom = extents["\u0E01\u0E35"][0], extents["\u0E01\u0E38"][1]
    return sheet, places, clusters, top, bottom, line_top, line_bottom


def kept_latin(folder):
    """The Latin half of the atlas as it stands, for --thai-only."""
    with open(os.path.join(folder, "tuner-font.json"), encoding="utf-8") as file:
        numbers = json.load(file)
    atlas = Image.open(os.path.join(folder, "tuner-font.png")).getchannel("A")
    bottom = max(y + h for x, y, w, h, *_ in numbers["glyphs"].values()) + PAD
    cell_height = numbers["lineHeight"] + PAD * 2
    rows = (bottom + cell_height - 1) // cell_height
    return numbers["ascent"], numbers["lineHeight"], numbers["glyphs"], atlas.crop((0, 0, atlas.width, rows * cell_height))


def main():
    folder = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets")
    if sys.argv[1] == "--thai-only":
        ascent, line_height, glyphs, latin_atlas = kept_latin(folder)
        sarabun = sys.argv[2]
    else:
        pieces = []
        ascent, line_height, glyphs = latin(pieces)
        latin_atlas = pieces[0]
        sarabun = sys.argv[2]
    sheet, places, clusters, top, bottom, line_top, line_bottom = thai(latin_atlas.width, sarabun)
    atlas = Image.new("L", (latin_atlas.width, latin_atlas.height + sheet.height), 0)
    atlas.paste(latin_atlas, (0, 0))
    atlas.paste(sheet, (0, latin_atlas.height))
    thai_glyphs = {str(glyph_id): [gx, gy + latin_atlas.height, gw, gh] for glyph_id, (gx, gy, gw, gh) in places.items()}

    white = Image.new("L", atlas.size, 255)
    Image.merge("RGBA", (white, white, white, atlas)).save(os.path.join(folder, "tuner-font.png"), optimize=True)
    with open(os.path.join(folder, "tuner-font.json"), "w", encoding="utf-8") as file:
        json.dump({"size": SIZE, "ascent": ascent, "lineHeight": line_height, "glyphs": glyphs,
                   "thai": {"ascent": round(line_top, 2), "descent": round(line_bottom, 2), "top": round(top, 2),
                            "bottom": round(bottom, 2), "glyphs": thai_glyphs, "clusters": clusters}},
                  file, separators=(",", ":"), ensure_ascii=False)
    print(f"wrote {len(glyphs)} Latin glyphs, line height {line_height}; {len(clusters)} Thai clusters from "
          f"{len(thai_glyphs)} glyphs, {top:.1f} px above the baseline and {bottom:.1f} below; atlas {atlas.size}")


if __name__ == "__main__":
    main()
