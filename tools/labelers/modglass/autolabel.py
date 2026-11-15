"""Label the glass and water of a pack's redrawn buildings automatically, in the mod glass tool's store.

    python tools/labelers/modglass/autolabel.py --mod "<pack's [CP] folder>" review
    python tools/labelers/modglass/autolabel.py --mod "<pack's [CP] folder>" write

Made for Elle's Town Buildings (15 Nov 2026), after the author painted the theater door by hand and
asked for the rest to be done the same way. It learns from what the author painted, so paint one
building by hand in the tool first and confirm it (A), then run this for the others.

How a tile is labelled, the way the shipped labels and the author's own paint do it:
  glass  the pane in the pack's own glass colours (learned from the author's confirmed tiles: the
         colours that sit mostly under their glass), plus white shine touching it, in groups of at
         least SMALLEST_PANE pixels; specks inside a pane up to SMALL_HOLE pixels are filled (shine,
         shading), anything bigger is left out (a sign hanging in a door). On the sheets named in
         --light-blue-sheets, any light blue inside the old label's glass also counts: some sheets
         paint their panes in another blue. Never name a sheet whose walls are light blue
         themselves (Alex's house and JojaMart are, and the rule took their bricks for glass).
  water  kept only where the pack still draws water (a blue clearly bluer than it is red).
Only tiles the tool lists as "to check" (shipped glass or water on changed art) are touched, and
never one the author has already saved. `review` writes review.png beside --out: the pack's art,
the old label and the new one per building. `write` saves them as drafts; confirm in the tool or
re-save as checked, then Export and run makevariant as the tool prints.
"""
import argparse, base64, collections, os, sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import modglass as mg  # noqa: E402

GLASSY, LIQUID = {8, 12, 13}, {1, 9, 10, 11, 14}
SMALL_HOLE = 12
SMALLEST_PANE = 6


def learn_glass_colours(tool):
    """The colours under the author's confirmed glass, kept when at least twice as common inside the
    glass as outside it on the same tiles, so a frame colour that touches a pane is not learned."""
    inside, outside = collections.Counter(), collections.Counter()
    for item_id, tiles in tool.overlay.items():
        picture = np.asarray(tool.picture(item_id))
        columns = picture.shape[1] // 16
        for tile, entry in tiles.items():
            if entry["status"] != "edited":
                continue
            row, column = divmod(tile, columns)
            block = picture[row * 16:row * 16 + 16, column * 16:column * 16 + 16]
            for p, value in enumerate(entry["cells"]):
                pixel = tuple(int(x) for x in block[p // 16, p % 16])
                if pixel[3] < 200:
                    continue
                (inside if value in GLASSY else outside)[pixel[:3]] += 1
    return {colour for colour, count in inside.items()
            if count >= 8 and count >= 2 * outside[colour] and min(colour) < 250}


def glass_mask(rgba, colours, blue_too=False, extra_seeds=None, cool_too=False):
    rgb = rgba[:, :, :3].astype(int)
    hit = np.zeros(rgba.shape[:2], bool)
    for colour in colours:
        hit |= np.all(rgb == colour, axis=2)
    hit &= rgba[:, :, 3] >= 200
    if extra_seeds is not None:
        hit |= extra_seeds
    white = (rgb.min(axis=2) >= 225) & (rgba[:, :, 3] >= 200)
    hit |= white & ndimage.binary_dilation(hit, iterations=1)
    labels, count = ndimage.label(hit)
    sizes = ndimage.sum(hit, labels, range(1, count + 1))
    keep = np.isin(labels, [i + 1 for i, s in enumerate(sizes) if s >= SMALLEST_PANE])
    # A tiny pane next to a real one is its sibling across a window bar (the top right pane of the
    # glass in Lewis's front door is 5 pixels).
    near_pane = ndimage.binary_dilation(keep, iterations=2)
    keep |= np.isin(labels, [i + 1 for i, s in enumerate(sizes) if s >= 3 and near_pane[labels == i + 1].any()])
    # Grow each pane out over the light, cool pixels it touches, stopping at the darker frame: a shop
    # window drawn in two blues was half found by colour alone (Pierre's, 15 Nov 2026).
    # Clearly bluer than red: a pale pink sign in JojaMart's window ("WOW") passed "not redder".
    light = (rgba[:, :, 3] >= 200) & (rgb.sum(axis=2) >= 500) & (rgb[:, :, 2] >= rgb[:, :, 0] + 12) & (rgb[:, :, 1] >= rgb[:, :, 0])
    if cool_too:
        # Greyer panes with sky and shelves behind them (the bathhouse): any cool pixel that is not
        # dark. Only on the --light-blue-sheets, and only reached by growing from a pane.
        light |= (rgba[:, :, 3] >= 200) & (rgb.sum(axis=2) >= 400) & (rgb[:, :, 2] >= rgb[:, :, 0] + 10) & (rgb[:, :, 2] >= rgb[:, :, 1] - 10)
    if blue_too:
        # Glass drawn as a run from light to deep blue (the Oasis skylight). Only for sheets named
        # in --blue-glass-sheets: on the outdoor sheet it climbed onto the Carpenter's blue roof.
        light |= (rgba[:, :, 3] >= 200) & (rgb[:, :, 2] >= 140) & (rgb[:, :, 2] > rgb[:, :, 0] + 40) & (rgb[:, :, 2] >= rgb[:, :, 1] - 10)
    grown_labels, grown_count = ndimage.label(light | keep)
    touched = np.unique(grown_labels[keep])
    keep = keep | (np.isin(grown_labels, touched[touched > 0]) & light)
    keep = drop_islands(keep)
    keep = drop_outlined_signs(keep)
    holes = ndimage.binary_fill_holes(keep) & ~keep
    hole_labels, hole_count = ndimage.label(holes)
    hole_sizes = ndimage.sum(holes, hole_labels, range(1, hole_count + 1))
    return keep | np.isin(hole_labels, [i + 1 for i, s in enumerate(hole_sizes) if s <= SMALL_HOLE])


def drop_islands(mask):
    """A patch of glass lying inside a hole of another pane is a sign or a poster drawn on the glass
    (JojaMart's door sign, its "WOW" card, the theater's sign): it is not glass. Windows split by
    bars are left alone, because their panes sit in the wooden frame, not in a hole of the glass."""
    labels, count = ndimage.label(mask)
    if count < 2:
        return mask
    sizes = ndimage.sum(mask, labels, range(1, count + 1))
    boxes = ndimage.find_objects(labels)
    out = mask.copy()
    for i in np.argsort(-sizes):
        box = boxes[i]
        if box is None or sizes[i] < 40:
            continue
        own = labels[box] == i + 1
        hole = ndimage.binary_fill_holes(own) & ~own
        if not hole.any():
            continue
        inside = np.unique(labels[box][hole])
        for j in inside:
            if j and j != i + 1:
                out[labels == j] = False
    return out


def drop_outlined_signs(mask, smallest_sign=20):
    """A sign drawn on the glass as an outline with gaps (JojaMart's door) leaves its inside joined
    to the pane. Close the outline by a pixel, and whatever it then encloses is the sign."""
    inside_panes = ndimage.binary_fill_holes(mask) & ~mask
    closed = ndimage.binary_dilation(inside_panes, iterations=1) & ndimage.binary_fill_holes(mask)
    enclosed = ndimage.binary_fill_holes(closed) & ~closed
    labels, count = ndimage.label(enclosed)
    if not count:
        return mask
    sizes = ndimage.sum(enclosed, labels, range(1, count + 1))
    signs = np.isin(labels, [i + 1 for i, s in enumerate(sizes) if s >= smallest_sign])
    return mask & ~ndimage.binary_dilation(signs, iterations=1)


def water_mask(rgba):
    rgb = rgba[:, :, :3].astype(int)
    return (rgba[:, :, 3] >= 200) & (rgb[:, :, 2] >= 140) & (rgb[:, :, 2] > rgb[:, :, 0] + 40) & (rgb[:, :, 2] >= rgb[:, :, 1])


def label_all(tool, colours, light_blue_sheets, all_changed=False, blue_glass_sheets=(), not_glass=(), glass_boxes=()):
    results = {}
    for item_id, item in tool.items.items():
        done = tool.overlay.get(item_id, {})
        todo = [t for t, k in item["tiles"].items() if (k == "glass" or all_changed) and t not in done]
        if not todo:
            continue
        picture = np.asarray(tool.picture(item_id))
        columns = picture.shape[1] // 16
        lenient = any(name in item["sheet"] for name in light_blue_sheets)
        seeds = None
        if lenient:
            # Light blue inside the old label's glass seeds the panes for the whole picture, so a pane
            # grows on into the next tile instead of stopping at the edge of the old label.
            seeds = np.zeros(picture.shape[:2], bool)
            rgbint = picture.astype(int)
            light_blue = (rgbint[:, :, 3] >= 200) & (rgbint[:, :, 2] >= 170) & (rgbint[:, :, 2] >= rgbint[:, :, 0] + 15) & (rgbint[:, :, :3].sum(axis=2) >= 450)
            for tile, kind in item["tiles"].items():
                old = tool.base[item["sheet"]].get(tile)
                if old is None or not any(v in GLASSY for v in old):
                    continue
                row, column = divmod(tile, columns)
                near = ndimage.binary_dilation(np.array([v in GLASSY for v in old]).reshape(16, 16), iterations=2)
                seeds[row * 16:row * 16 + 16, column * 16:column * 16 + 16] |= near & light_blue[row * 16:row * 16 + 16, column * 16:column * 16 + 16]
        glass = glass_mask(picture, colours, any(n in item["sheet"] for n in blue_glass_sheets), seeds, lenient)
        # Signs drawn in glass colours on the glass (JojaMart's door plate) cannot be told from the
        # pane by colour: the author points them out and their boxes are cleared.
        cleared = np.zeros(picture.shape[:2], bool)
        for name, (x0, y0, x1, y1) in not_glass:
            if name in item_id:
                cleared[y0:y1, x0:x1] = True
        # Windows that show the room behind the glass (a shelf, a dark interior, a green tint): the
        # shipped labels cover the whole pane, so the reviewer gives the pane's box. Everything in it
        # but the frame's colours (those on the line just outside the box) is glass.
        opaque = picture[:, :, 3] >= 200
        packed = (picture[:, :, 0].astype(int) << 16) | (picture[:, :, 1].astype(int) << 8) | picture[:, :, 2].astype(int)
        for name, (x0, y0, x1, y1) in glass_boxes:
            if name not in item_id:
                continue
            ring = np.concatenate([packed[y0 - 1, x0 - 1:x1 + 1], packed[y1, x0 - 1:x1 + 1],
                                   packed[y0:y1, x0 - 1], packed[y0:y1, x1]])
            values, counts = np.unique(ring, return_counts=True)
            frame = values[counts >= 0.15 * counts.sum()]
            glass[y0:y1, x0:x1] |= opaque[y0:y1, x0:x1] & ~np.isin(packed[y0:y1, x0:x1], frame)
        glass &= ~cleared
        # Water grows from where the old label had it over all the water the pack draws touching it,
        # so a pool the pack made longer (the bathhouse) is covered whole, not cut at the old tiles.
        old_liquid = np.zeros(picture.shape[:2], np.uint8)
        for tile in item["tiles"]:
            old = tool.base[item["sheet"]].get(tile)
            if old is None:
                continue
            row, column = divmod(tile, columns)
            cells = np.frombuffer(old, np.uint8).reshape(16, 16)
            old_liquid[row * 16:row * 16 + 16, column * 16:column * 16 + 16] = np.where(np.isin(cells, list(LIQUID)), cells, 0)
        # Besides the plain water colour, the shaded water under a deck or behind a pillar and the light
        # ripples count, but only joined to the water (the bathhouse pool lost both edges without them).
        rgbint = picture.astype(int)
        opaque = rgbint[:, :, 3] >= 200
        shaded = opaque & (rgbint[:, :, 2] >= 100) & (rgbint[:, :, 2] >= rgbint[:, :, 0] + 30) & (rgbint[:, :, 2] >= rgbint[:, :, 1] + 20)
        ripples = opaque & (rgbint[:, :, :3].sum(axis=2) >= 600) & (rgbint[:, :, 2] >= rgbint[:, :, 0] + 40)
        plain = water_mask(picture)
        joined_labels, _ = ndimage.label(plain | shaded | ripples)
        joined = np.unique(joined_labels[plain])
        joined_water = np.isin(joined_labels, joined[joined > 0])
        # Shaded water cut off from the pool by a pillar in front of it: a small patch close beside it.
        # A blue roof near a pond is bigger and further away.
        apart_labels, apart_count = ndimage.label((shaded | ripples) & ~joined_water)
        if apart_count:
            near_water = ndimage.binary_dilation(joined_water, iterations=12)
            apart_sizes = ndimage.sum(np.ones_like(apart_labels), apart_labels, range(1, apart_count + 1))
            close = np.unique(apart_labels[near_water & (apart_labels > 0)])
            joined_water |= np.isin(apart_labels, [j for j in close if j and 12 <= apart_sizes[j - 1] <= 400])
        water_labels, water_count = ndimage.label(joined_water)
        near_old_liquid = ndimage.grey_dilation(old_liquid, size=(5, 5))
        water_kind = np.zeros(picture.shape[:2], np.uint8)
        for index, box in enumerate(ndimage.find_objects(water_labels)):
            if box is None:
                continue
            own = water_labels[box] == index + 1
            kinds = collections.Counter(int(v) for v in near_old_liquid[box][own] if v)
            if kinds and own.sum() >= 12:     # a few water-blue pixels on a railing are not a pool
                water_kind[box][own] = kinds.most_common(1)[0][0]
        # Patches joined only by being close take the kind of the water beside them.
        nearby_kind = ndimage.grey_dilation(water_kind, size=(25, 25))
        for index, box in enumerate(ndimage.find_objects(water_labels)):
            if box is None:
                continue
            own = water_labels[box] == index + 1
            if not water_kind[box][own].any() and own.sum() >= 12 and nearby_kind[box][own].any():
                water_kind[box][own] = collections.Counter(int(v) for v in nearby_kind[box][own] if v).most_common(1)[0][0]
        # White ripple dashes inside a pond or a trough (Marnie's) are part of the water.
        for kind in np.unique(water_kind[water_kind > 0]):
            holes = ndimage.binary_fill_holes(water_kind == kind) & (water_kind == 0)
            hole_labels, hole_count = ndimage.label(holes)
            hole_sizes = ndimage.sum(holes, hole_labels, range(1, hole_count + 1))
            water_kind[np.isin(hole_labels, [i + 1 for i, size in enumerate(hole_sizes) if size <= SMALL_HOLE])] = kind
        out = {}
        for tile in todo:
            old = tool.base[item["sheet"]].get(tile, bytes(256))
            row, column = divmod(tile, columns)
            g = glass[row * 16:row * 16 + 16, column * 16:column * 16 + 16].reshape(-1).copy()
            w = water_kind[row * 16:row * 16 + 16, column * 16:column * 16 + 16].reshape(-1)
            if lenient:
                block = picture[row * 16:row * 16 + 16, column * 16:column * 16 + 16].reshape(-1, 4).astype(int)
                near_old = ndimage.binary_dilation(np.array([v in GLASSY for v in old]).reshape(16, 16), iterations=2).reshape(-1)
                r, gr, b, a = block[:, 0], block[:, 1], block[:, 2], block[:, 3]
                g |= near_old & (a >= 200) & (b >= 170) & (b >= r + 15) & (r + gr + b >= 450) & ~cleared[row * 16:row * 16 + 16, column * 16:column * 16 + 16].reshape(-1)
            glass_kind = collections.Counter(v for v in old if v in GLASSY).most_common(1) or [(13, 0)]
            wet = any(v in LIQUID for v in old) or w.any()
            if not any(g) and not w.any():
                if tile not in item["tiles"] or item["tiles"][tile] != "glass":
                    continue           # a changed tile with no glass in the pack's art stays as shipped
            new = bytearray(v if v not in GLASSY and v not in LIQUID else 0 for v in old)
            for p in range(256):
                if w[p]:
                    new[p] = w[p]
                elif g[p] and not wet:      # a water tile's light ripples are not glass
                    new[p] = glass_kind[0][0]
            out[tile] = bytes(new)
        # A pixel or two of glass on its own is a stray light speck the per tile rule picked up (the
        # Carpenter's roof had five), not a pane.
        final = np.zeros(picture.shape[:2], bool)
        for tile, cells in out.items():
            row, column = divmod(tile, columns)
            final[row * 16:row * 16 + 16, column * 16:column * 16 + 16] = np.isin(np.frombuffer(cells, np.uint8).reshape(16, 16), list(GLASSY))
        speck_labels, speck_count = ndimage.label(final)
        speck_sizes = ndimage.sum(final, speck_labels, range(1, speck_count + 1))
        specks = np.isin(speck_labels, [i + 1 for i, size in enumerate(speck_sizes) if size < 4])
        for tile in list(out):
            row, column = divmod(tile, columns)
            drop = specks[row * 16:row * 16 + 16, column * 16:column * 16 + 16].reshape(-1)
            if drop.any():
                out[tile] = bytes(0 if drop[p] else v for p, v in enumerate(out[tile]))
        if out:
            results[item_id] = out
    return results


def review(tool, results, path):
    panels = []
    for item_id, out in results.items():
        item = tool.items[item_id]
        picture = tool.picture(item_id)
        columns = picture.width // 16
        tiles = list(out)
        xs = [(t % columns) * 16 for t in tiles]; ys = [(t // columns) * 16 for t in tiles]
        x0, y0 = max(0, min(xs) - 24), max(0, min(ys) - 24)
        x1, y1 = min(picture.width, max(xs) + 40), min(picture.height, max(ys) + 40)
        crop = picture.crop((x0, y0, x1, y1))

        def overlay(source):
            layer = Image.new("RGBA", crop.size, (0, 0, 0, 0)); px = layer.load()
            for t in tiles:
                tx, ty = (t % columns) * 16 - x0, (t // columns) * 16 - y0
                for p, v in enumerate(source(t)):
                    x, y = tx + p % 16, ty + p // 16
                    if 0 <= x < crop.width and 0 <= y < crop.height:
                        if v in GLASSY: px[x, y] = (255, 0, 200, 160)
                        elif v in LIQUID: px[x, y] = (0, 120, 255, 160)
            return Image.alpha_composite(crop, layer)
        scale = 3 if max(crop.size) < 260 else 2
        trio = [crop, overlay(lambda t: tool.base[item["sheet"]].get(t, bytes(256))), overlay(lambda t: out[t])]
        panels.append((f"{item['building']} - {len(tiles)} tiles   (pack art | old label | new label)",
                       [im.resize((im.width * scale, im.height * scale), Image.NEAREST) for im in trio]))
    width, rows, y = 1900, [], 0
    for title, trio in panels:
        s = min(1.0, width / (sum(im.width for im in trio) + 20))
        trio = [im.resize((int(im.width * s), int(im.height * s)), Image.NEAREST) for im in trio]
        rows.append((title, trio, y)); y += trio[0].height + 34
    sheet = Image.new("RGB", (width, y + 10), (30, 30, 30)); draw = ImageDraw.Draw(sheet)
    for title, trio, top in rows:
        draw.text((4, top + 4), title, fill="white"); x = 0
        for im in trio:
            sheet.paste(im, (x, top + 22)); x += im.width + 10
    sheet.save(path)


def boxes(text):
    """'<building id part>:x0,y0,x1,y1;...' as (name, (x0, y0, x1, y1)) pairs."""
    pairs = []
    for entry in text.split(";"):
        if entry.strip():
            name, numbers = entry.rsplit(":", 1)
            pairs.append((name, tuple(int(v) for v in numbers.split(","))))
    return pairs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["review", "write"])
    parser.add_argument("--mod", required=True)
    parser.add_argument("--palette", default=None)
    parser.add_argument("--season", default="summer")
    parser.add_argument("--store", default=os.path.join(mg.REPO, "..", "_label-store", "modglass"))
    parser.add_argument("--light-blue-sheets", default="", help="comma separated sheet name parts, e.g. outdoorsTileSheet,DesertTiles")
    parser.add_argument("--out", default=".")
    parser.add_argument("--blue-glass-sheets", default="", help="sheets whose glass runs into deep blue, e.g. DesertTiles")
    parser.add_argument("--extra-glass-colours", default="", help="glass colours to add to the learned ones, 'r,g,b;r,g,b'")
    parser.add_argument("--confirmed", action="store_true", help="write: save as confirmed (only after the author has looked at the review)")
    parser.add_argument("--glass-box", default="", help="panes that show the room behind them, inside the frame, in picture pixels: '<building id part>:x0,y0,x1,y1;...'")
    parser.add_argument("--not-glass", default="", help="boxes that are signs, not glass, in picture pixels: '<building id part>:x0,y0,x1,y1;...'")
    parser.add_argument("--all-changed", action="store_true", help="also give glass to windows the pack drew where the base game had none")
    args = parser.parse_args()
    probe = mg.Pack(args.mod, args.palette or "")
    palette = args.palette or probe.config.get("Palette") or (probe.palettes[0] if probe.palettes else "")
    pack = mg.Pack(args.mod, palette)
    slug = "".join(ch if ch.isalnum() else "-" for ch in pack.manifest.get("Name", "pack").lower()).strip("-")
    while "--" in slug:
        slug = slug.replace("--", "-")
    tool = mg.ModGlass(pack, args.season, os.path.join(args.store, slug, palette or "default"))
    colours = learn_glass_colours(tool)
    colours |= {tuple(int(v) for v in c.split(",")) for c in args.extra_glass_colours.split(";") if c}
    if not colours:
        sys.exit("no confirmed glass to learn from: paint and confirm one building in the tool first")
    print(f"learned {len(colours)} glass colours from the confirmed tiles: {sorted(colours)}")
    results = label_all(tool, colours, [s for s in args.light_blue_sheets.split(",") if s], args.all_changed, [s for s in args.blue_glass_sheets.split(",") if s],
                        boxes(args.not_glass), boxes(args.glass_box))
    print(f"{len(results)} buildings, {sum(len(v) for v in results.values())} tiles")
    if args.mode == "review":
        path = os.path.join(args.out, "review.png")
        review(tool, results, path)
        print("review written to", os.path.abspath(path))
    else:
        for item_id, out in results.items():
            def status(tile, cells):
                if not args.confirmed:
                    return "draft"
                return "checked" if cells == tool.base[tool.items[item_id]["sheet"]].get(tile, bytes(256)) else "edited"
            body = {"id": item_id, "tiles": {str(t): {"classes": base64.b64encode(c).decode(), "status": status(t, c)} for t, c in out.items()}}
            print(item_id, tool.save_tiles(body)["summary"]["counts"])


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
