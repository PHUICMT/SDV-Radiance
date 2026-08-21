"""Read a tBIN map: tilesheets, layers, and every cell's tile index and properties.

xTile's own binary format, which is what most Stardew map packs ship. Written because the
orientation check could only read .tmx, leaving 1,937 map files in this library unverifiable,
and .tmx and .tbin are not different populations - the same authors ship both.

    from tbin import read
    document = read(path)
    document.layers[0].cells[(x, y)]      -> Cell(index, properties, frames)

FORMAT, as xTile writes it. Everything is little endian.

    "tBIN10"                                  6 byte magic
    string   map id
    string   map description
    props    map properties
    int32    tilesheet count, then per sheet:
        string id, string description, string image source,
        size sheet (2 int32), size tile, size margin, size spacing, props
    int32    layer count, then per layer:
        string id, bool visible, string description,
        size layer (in tiles), size tile, props,
        then cells, read left to right and top to bottom until the layer is full:
            'T' string        the tilesheet the following tiles come from
            'N' int32         that many empty cells
            'S' int32 props   one tile, by index into the current sheet
            'A' int32 int32   an animated tile: frame interval, frame count,
                              then that many frames each written as a static tile,
                              then the animated tile's own props

    string   int32 length then that many UTF-8 bytes
    props    int32 count, then per property: string key, byte type, value
             type 0 bool (1 byte), 1 int32, 2 float, 3 string

WHAT THIS ESTABLISHED, which turned out to be the point of writing it.

Run over the whole library: 1,897 files read, 169,619 animated cells parsed, and ZERO cells
carrying @Flip or @Rotation. That is not a gap in the reader, it is the format: @Flip and
@Rotation are written by TMXTile while it loads a .tmx, and tBIN has no flip or rotate of its
own, so a turned tile cannot exist in one.

So orientation only ever arrives from .tmx, and verifying .tmx covers the whole of it. The
1,937 .tbin files were never an unverified remainder; there was nothing in them to verify.

The reader still earns its place for checking CELLS - which tile index a .tbin map places where
- which nothing could read before.
"""
import struct
import sys
from collections import namedtuple

MAGIC = b"tBIN10"


class NotAMap(ValueError):
    """The file is not a map at all, as opposed to a map that could not be read."""


Cell = namedtuple("Cell", "index sheet properties frames")
Layer = namedtuple("Layer", "id visible description width height tile_width tile_height "
                            "properties cells")
Sheet = namedtuple("Sheet", "id description image_source columns rows tile_width tile_height")
Document = namedtuple("Document", "id description properties sheets layers")


class Reader:
    """A cursor over the bytes, so a malformed file fails where it went wrong."""

    def __init__(self, data):
        self.data = data
        self.at = 0

    def take(self, count):
        if self.at + count > len(self.data):
            raise ValueError(f"ran off the end at byte {self.at} wanting {count}")
        chunk = self.data[self.at:self.at + count]
        self.at += count
        return chunk

    def byte(self):
        return self.take(1)[0]

    def int32(self):
        return struct.unpack("<i", self.take(4))[0]

    def single(self):
        return struct.unpack("<f", self.take(4))[0]

    def string(self):
        length = self.int32()
        if length < 0 or length > 1 << 24:
            raise ValueError(f"absurd string length {length} at byte {self.at - 4}")
        return self.take(length).decode("utf-8", errors="replace")

    def size(self):
        return self.int32(), self.int32()

    def properties(self):
        out = {}
        for _ in range(self.int32()):
            key = self.string()
            kind = self.byte()
            if kind == 0:
                value = self.byte() != 0
            elif kind == 1:
                value = self.int32()
            elif kind == 2:
                value = self.single()
            elif kind == 3:
                value = self.string()
            else:
                raise ValueError(f"unknown property type {kind} for '{key}' at byte {self.at}")
            out[key] = value
        return out


def read(path):
    with open(path, "rb") as handle:
        data = handle.read()
    if data[:4] == b"\x00\x05\x16\x07":
        # An AppleDouble sidecar: macOS writes ._Name.tbin beside the real file to carry the
        # resource fork, and it survives being zipped. 40 of them in this library. Not a map,
        # and not a broken map either, so it must not be reported as one.
        raise NotAMap(f"AppleDouble sidecar, not a map: {path}")
    if not data.startswith(MAGIC):
        raise ValueError(f"not a tBIN10 file: starts {data[:6]!r}")
    reader = Reader(data)
    reader.at = len(MAGIC)

    map_id = reader.string()
    description = reader.string()
    map_properties = reader.properties()

    sheets = []
    for _ in range(reader.int32()):
        sheet_id = reader.string()
        sheet_description = reader.string()
        image_source = reader.string()
        columns, rows = reader.size()
        tile_width, tile_height = reader.size()
        reader.size()                       # margin
        reader.size()                       # spacing
        reader.properties()
        sheets.append(Sheet(sheet_id, sheet_description, image_source,
                            columns, rows, tile_width, tile_height))

    layers = []
    for _ in range(reader.int32()):
        layer_id = reader.string()
        visible = reader.byte() != 0
        layer_description = reader.string()
        width, height = reader.size()
        tile_width, tile_height = reader.size()
        layer_properties = reader.properties()

        cells = {}
        current_sheet = None
        position = 0
        total = width * height
        while position < total:
            command = reader.byte()
            if command == ord("T"):
                current_sheet = reader.string()
            elif command == ord("N"):
                position += reader.int32()
            elif command == ord("S"):
                index = reader.int32()
                reader.byte()                        # blend mode, one byte between the two
                cells[(position % width, position // width)] = Cell(
                    index, current_sheet, reader.properties(), None)
                position += 1
            elif command == ord("A"):
                reader.int32()                       # frame interval, in milliseconds
                frame_count = reader.int32()
                frames = []
                frame_sheet = current_sheet
                for _ in range(frame_count):
                    inner = reader.byte()
                    if inner == ord("T"):
                        frame_sheet = reader.string()
                        inner = reader.byte()
                    if inner != ord("S"):
                        raise ValueError(f"animation frame is not a static tile at byte {reader.at}")
                    frame_index = reader.int32()
                    reader.byte()                    # blend mode, as on a static tile
                    frames.append(Cell(frame_index, frame_sheet, reader.properties(), None))
                # The orientation lives on the ANIMATED tile, not on its frames, which is the
                # same rule the dump follows.
                cells[(position % width, position // width)] = Cell(
                    frames[0].index if frames else -1, frame_sheet, reader.properties(), frames)
                position += 1
            else:
                raise ValueError(f"unknown cell command {command!r} at byte {reader.at - 1}")
        layers.append(Layer(layer_id, visible, layer_description, width, height,
                            tile_width, tile_height, layer_properties, cells))

    return Document(map_id, description, map_properties, sheets, layers)


def orientation(properties):
    """@Flip and @Rotation as one byte, exactly as MapLayers.Orientation does it in the mod.

    Deliberately a SECOND implementation rather than a shared one: a check that runs the code
    it is checking proves only that the code is self-consistent. If this and the mod disagree,
    one of them is wrong and that is worth knowing.
    """
    turns = 0
    flip = 0
    rotation = properties.get("@Rotation")
    if rotation is not None:
        try:
            degrees = int(str(rotation).strip())
        except ValueError:
            degrees = 0
        degrees = ((degrees % 360) + 360) % 360
        if degrees in (90, 180, 270):
            turns = degrees // 90
        elif degrees in (1, 2, 3):
            turns = degrees
    marker = properties.get("@Flip")
    if marker is not None:
        text = str(marker).strip()
        if text in ("1", "true", "True"):
            flip = 1
        elif text == "2":
            flip = 2
    if flip == 2:
        turns = (turns + 2) & 3
        flip = 1
    return (4 if flip else 0) | turns


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    for path in sys.argv[1:]:
        try:
            document = read(path)
        except Exception as error:
            print(f"{path}: {error}")
            continue
        turned = sum(1 for layer in document.layers for cell in layer.cells.values()
                     if orientation(cell.properties))
        print(f"{path}\n  id={document.id!r}  {len(document.sheets)} sheet(s)  "
              f"{len(document.layers)} layer(s)  turned cells: {turned}")
        for layer in document.layers:
            print(f"    {layer.id:<12} {layer.width}x{layer.height}  {len(layer.cells)} cell(s)")
