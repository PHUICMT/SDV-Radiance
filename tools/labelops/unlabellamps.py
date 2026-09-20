"""Take the mirror label off the round lamps on the town's parked vehicles.

A headlight is glass, so it was labelled, and the mirror class returns a body at full strength: a
truck parked in the street showed a clear little person in each of its lamps, which reads as a
gimmick rather than as light on glass (the author's call, 20 Sep 2026). The windscreen and the wing
mirrors keep their labels; only the lamp blobs go.

The cells are the same in all four seasons of the town sheet, so all four are walked.

    python tools/labelops/unlabellamps.py [--write]
"""
import base64
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LABELS = os.path.join(REPO, "labels", "water-labels.json")
# The lamp blobs, by sheet cell: each headlight straddles two or four cells.
LAMP_CELLS = ["773", "774", "805", "806", "1932", "1933", "1935", "1964", "1965", "1967"]
SHEETS = ["spring_town", "summer_town", "fall_town", "winter_town"]


def main(write):
    labels = json.load(open(LABELS, encoding="utf-8"))
    classes = labels["classes"]
    mirror = classes.index("mirror")
    ground = classes.index("ground")
    changed = 0
    for sheet_name in SHEETS:
        sheet = labels["sheets"].get(sheet_name)
        if sheet is None:
            continue
        for cell_index in LAMP_CELLS:
            cell = sheet["tiles"].get(cell_index)
            if cell is None:
                continue
            grid = bytearray(base64.b64decode(cell))
            lamp_pixels = sum(1 for value in grid if value == mirror)
            if lamp_pixels == 0:
                continue
            for position, value in enumerate(grid):
                if value == mirror:
                    grid[position] = ground
            sheet["tiles"][cell_index] = base64.b64encode(bytes(grid)).decode("ascii")
            changed += lamp_pixels
            print(f"{sheet_name} cell {cell_index}: {lamp_pixels} lamp pixel(s) cleared")
    print(f"{changed} pixel(s) in total")
    if write and changed:
        with open(LABELS, "w", encoding="utf-8", newline="\n") as file:
            json.dump(labels, file, ensure_ascii=False, separators=(",", ":"))
        print("written to", LABELS)
    elif not write:
        print("dry run: pass --write to save")


if __name__ == "__main__":
    main("--write" in sys.argv)
