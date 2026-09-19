"""Write labels/sprite-glass.json: glass painted on art a location draws for itself, not on a map.

The bus at the bus stop is not a tile. BusStop.draw paints it straight from LooseSprites/Cursors
every frame, and it drives away, so no map label can reach its windows. The window reflection reads
glass for such art through the same sheet-cell labels a map tile uses (LabelStore.GetSheetCell),
keyed by the sheet the draw came from. This writes those cells for the bus.

The glass is read from the art rather than drawn by hand: every pixel of the glass palette (the
pale cyan of the panes, and the hill and the dark green painted inside them) joined to at least a
few cyan pixels is glass; the frames, the sills and the green star on the livery are not. The
door is left out, because the door is a separate sprite the game draws over that part of the bus.

    python tools/labelops/spriteglass.py [path to the unpacked Cursors.png]
"""
import base64, json, os, sys
from collections import deque

from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_CURSORS = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Content (unpacked)\LooseSprites\Cursors.png"
OUT = os.path.join(REPO, "labels", "sprite-glass.json")

WATER_LABELS = os.path.join(REPO, "labels", "water-labels.json")
GLASS = 13
BUS_SOURCE = (288, 1247, 128, 64)          # BusStop.busSource
BUS_DOOR_COLUMNS = range(16, 32)           # busDoor is drawn at bus + (16, 26) source pixels, 16 wide
SMALLEST_PANE = 12
CYAN_PIXELS_IN_A_PANE = 8


def is_cyan(pixel):
    red, green, blue, alpha = pixel
    return alpha > 0 and green >= 180 and blue >= 170 and red <= 200


def is_glass_palette(pixel):
    red, green, blue, alpha = pixel
    if alpha == 0:
        return False
    if is_cyan(pixel):
        return True
    # What the panes show: the green hill and the dark trees and shade painted into them.
    return (red, green, blue) in {(103, 139, 91), (0, 50, 0), (3, 40, 35)}


def glass_mask(image):
    left, top, width, height = BUS_SOURCE
    pixels = image.load()
    at = lambda x, y: pixels[left + x, top + y]
    seen = [[False] * width for _ in range(height)]
    mask = [[False] * width for _ in range(height)]
    for start_y in range(height):
        for start_x in range(width):
            if seen[start_y][start_x] or not is_glass_palette(at(start_x, start_y)):
                continue
            component, queue = [], deque([(start_x, start_y)])
            seen[start_y][start_x] = True
            while queue:
                x, y = queue.popleft()
                component.append((x, y))
                for next_x, next_y in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                    if 0 <= next_x < width and 0 <= next_y < height and not seen[next_y][next_x] \
                            and is_glass_palette(at(next_x, next_y)):
                        seen[next_y][next_x] = True
                        queue.append((next_x, next_y))
            cyan = sum(1 for x, y in component if is_cyan(at(x, y)))
            in_door = all(x in BUS_DOOR_COLUMNS for x, _ in component)
            if len(component) >= SMALLEST_PANE and cyan >= CYAN_PIXELS_IN_A_PANE and not in_door:
                for x, y in component:
                    mask[y][x] = True
    return mask


def main():
    cursors = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CURSORS
    image = Image.open(cursors).convert("RGBA")
    mask = glass_mask(image)
    left, top, width, height = BUS_SOURCE
    cells_across = image.width // 16
    tiles = {}
    for y in range(height):
        for x in range(width):
            if not mask[y][x]:
                continue
            sheet_x, sheet_y = left + x, top + y
            index = (sheet_y // 16) * cells_across + sheet_x // 16
            cell = tiles.setdefault(index, bytearray(256))
            cell[(sheet_y % 16) * 16 + sheet_x % 16] = GLASS
    classes = json.load(open(WATER_LABELS, encoding="utf-8"))["classes"]
    document = {
        "format": "16x16-classes-base64",
        "classes": classes,
        "sheets": {
            "Cursors": {
                "size": [image.width, image.height],
                "tiles": {str(index): base64.b64encode(bytes(cell)).decode("ascii") for index, cell in sorted(tiles.items())},
            }
        },
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, indent=1)
        handle.write("\n")
    glass_pixels = sum(row.count(True) for row in mask)
    print(f"wrote {OUT}: {len(tiles)} cells, {glass_pixels} glass pixels on the bus")
    preview = Image.new("RGBA", (width, height))
    for y in range(height):
        for x in range(width):
            preview.putpixel((x, y), (255, 0, 0, 255) if mask[y][x] else image.getpixel((left + x, top + y)))
    return preview


if __name__ == "__main__":
    preview = main()
    if len(sys.argv) > 2:
        preview.resize((preview.width * 8, preview.height * 8), Image.NEAREST).save(sys.argv[2])
