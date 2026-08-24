"""Label the tiles that are pixel-for-pixel a tile somebody already labelled.

    python tools/labelops/twinlabels.py            measure and report, change nothing
    python tools/labelops/twinlabels.py --apply    write the exact twins into the labels
    python tools/labelops/twinlabels.py --suggest  also write the near misses for the labeller
    python tools/labelops/twinlabels.py --recolour also carry labels onto the sheets that are a
                                                   base-game sheet repainted (see recolour_copies)

Mods reuse art. A pack that adds a pond copies the base game's water tiles into its own sheet,
and that sheet then reads as unlabelled work when the answer for every one of its tiles is
already painted somewhere else. This finds those and copies the label across.

WHAT THIS ADDS OVER THE LABELLER'S OWN TWIN BUTTON, which does the same job in the page and did
it first: a count before anything is written, over every sheet in the dump rather than the ones
loaded; the rule that all versions of a sheet NAME must agree before a label is copied, which
matters because one label file serves the base game's spring_town and every recolour of it; and
the near misses, which the button does not offer at all. Use the button to paint, this to see
what painting would do and to get the list of tiles nothing can decide on its own.

FOUR GUARDS, because a wrong label is worse than an unpainted one and each of these was
measured rather than assumed.

  0. The same rule the labeller's own twin button uses for what "identical" means, so the two
     never disagree about whether two tiles are the same picture.
  1. Identical PIXELS, not a similar shape. Matching on the alpha silhouette finds ten times as
     many tiles and 36% of them disagree with each other about what they are (measured over the
     22 August corpus: 5,267 matches, 1,873 contradictory). Those go to --suggest, for a person
     to accept, and never to --apply.
  2. Every twin must agree on the WHOLE label, byte for byte, not merely on which class is
     commonest. A tile whose water reaches a different corner is a different answer.
  3. EVERY version of the sheet must reach the same answer. Labels are keyed by sheet NAME, so
     one file serves the base game's spring_town and every recolour of it, and a label written
     for one is read for all. So each version of the tile is looked up separately and the copy
     happens only when all of them found a twin and every twin agrees. A recolour that repaints
     water green still resolves to water and still copies; a name two mods use for genuinely
     different pictures does not.
  4. Only tiles a map actually PLACES. Sheets carry hundreds of tiles nobody ever draws, and
     labelling those is work that changes no pixel in the game.

Class ids are copied as raw bytes. Both class lists in use are the same list, one of them
truncated (the shorter is missing `glass` and `hot`), so the ids mean the same thing in both;
files written here always carry the full list.
"""
import argparse, base64, collections, glob, hashlib, io, json, os, shutil, sys, time

import numpy
from PIL import Image

from modsheets import VANILLA_SHEET_NAMES, base_game_art

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
HFDIR = os.environ.get("HF_STUDIO_DIR") or os.path.expanduser(r"~\Documents\HF-Studio")
# The labels do not have to live beside the dump: separating them is what lets this be proved
# against an archived dump while a sweep is writing the live one.
LABELDIR = os.environ.get("HF_LABELS_DIR") or HFDIR
TILE = 16
CELL_SHEET_STRIDE = 0x100000
CLASSES = ["ground", "water", "wall", "roof", "deck", "void", "emissive", "reflect_floor",
           "mirror", "ice", "flowing", "lava", "window", "glass", "hot"]
SKIP_SHEET = ("shadow", "darkness", "mask", "lighting", "_ore", "colors", "sky")
# The names the base game has for a tilesheet, from modsheets so all three tools answer this the
# same way. A disagreement about what a picture is gets settled in favour of an answer painted on
# one of these: the base game is the reference every mod is a variation of, and its sheets are the
# ones the whole corpus shares.
ALPHA_FLOOR = 8
# Below this many placed tiles a whole-sheet match is a coincidence rather than a repaint, and
# four fifths is where the pairing stops being about the same sheet and starts being about two
# sheets cut on the same grid.
RECOLOUR_MINIMUM_TILES = 8
RECOLOUR_SHARE = 0.80


def load_index():
    path = os.path.join(HFDIR, "maps.json")
    if not os.path.exists(path):
        sys.exit(f"no dump at {path}")
    with io.open(path, encoding="utf-8") as handle:
        return json.load(handle)


def label_files():
    """sheet -> the newest file holding its labels. The live folder at the HF-Studio root wins
    over labels/, which is where the labeller writes and where an older copy can sit."""
    found = {}
    for folder in (LABELDIR, os.path.join(LABELDIR, "labels")):
        for path in glob.glob(os.path.join(folder, "*.labels.json")):
            sheet = os.path.basename(path)[:-len(".labels.json")]
            found.setdefault(sheet, path)
    return found


def read_labels(path):
    try:
        with io.open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def sheet_versions(index):
    """sheet name -> every distinct PNG the dump holds for it.

    A sheet name is not one picture: spring_town is the base game's, and every recolour's, and
    the dump keeps them apart by content hash. Guard 3 needs all of them.
    """
    versions = collections.defaultdict(set)
    for sheet, art in (index.get("artPng") or {}).items():
        if art:
            versions[sheet].add(art)
    for key, entry in index["locations"].items():
        document = read_location(entry)
        for sheet, art in zip(document.get("sheets", []), document.get("sheetArt", [])):
            if art:
                versions[sheet].add(art)
    return versions


def read_location(entry):
    try:
        with io.open(os.path.join(HFDIR, entry["file"]), encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return {}


class Art:
    """Tile blocks out of the sheet PNGs, read once and not all at once.

    A decoded sheet is a couple of megabytes and the corpus has thousands of them, so holding
    every one that was ever opened is several gigabytes by the end of a full run - which is
    fine on the dump this was written against and not fine on a complete one. Both loops here
    work through one sheet at a time, so a small cache keeps every hit that matters; the bound
    exists to stop the total, not to speed anything up.
    """

    CACHED_SHEETS = 64

    def __init__(self, index):
        self.index = index
        self.cache = collections.OrderedDict()

    def pixels(self, relative_path):
        if relative_path in self.cache:
            self.cache.move_to_end(relative_path)
            return self.cache[relative_path]
        full = os.path.join(HFDIR, relative_path)
        try:
            image = numpy.array(Image.open(full).convert("RGBA"))
        except Exception:
            image = None
        self.cache[relative_path] = image
        while len(self.cache) > self.CACHED_SHEETS:
            self.cache.popitem(last=False)
        return image

    def block(self, relative_path, sheet, tile):
        image = self.pixels(relative_path)
        if image is None:
            return None
        width = (self.index.get("artDim", {}).get(sheet) or [image.shape[1]])[0]
        columns = max(1, width // TILE)
        y, x = divmod(tile, columns)
        piece = image[y * TILE:(y + 1) * TILE, x * TILE:(x + 1) * TILE]
        return piece if piece.shape[:2] == (TILE, TILE) else None


def is_filler(block):
    """A flat fill is not a picture, and matching one is evidence of nothing.

    Solid black is solid black on every sheet that has it, and 1,042 tiles of z_blank are exactly
    that. Copying a label between two of them looks like a match and is a guess: the same square
    of nothing is a void on one sheet and the inside of a dark wall on another, and no number of
    identical pixels can say which. vanillacopies.py refuses these and the labeller's own twin
    index has refused them for longer; this did not, which made one tool disagree with the other
    two about what counts as the same tile.
    """
    visible = block[block[:, :, 3] > ALPHA_FLOOR]
    if visible.size == 0:
        return True
    return len(numpy.unique(visible.reshape(-1, visible.shape[-1]), axis=0)) < 3


def exact_key(block):
    """What a tile looks like, with the colour under invisible pixels thrown away.

    The RGB beneath a fully transparent pixel is whatever the exporter left there, and it
    differs between two exports of the same art, so hashing it apart makes true twins look
    different. The labeller's own twin index has always normalised it away; this did not, and
    quietly under-counted every match. Same rule here so the two agree.
    """
    normalised = block.copy()
    normalised[block[:, :, 3] == 0] = 0
    return hashlib.sha1(normalised.tobytes()).hexdigest()


def shading_key(block):
    """What a repaint keeps: the outline, and which pixels are darker than which.

    Colour-blind on purpose, and returned as two digests so the outline can be compared on its
    own. A recolour shifts hue and level and leaves the shading where it was, so the ordering
    survives a palette swap; a different picture does not survive it.

    Quantised to four levels because a repaint is not always a clean multiply: it is hand-work,
    and two neighbouring shades can swap places by a value or two without the drawing changing.
    Four levels is coarse enough to ride that out and fine enough that a tile of grass and a tile
    of roof do not land on the same answer.
    """
    alpha = block[:, :, 3] > ALPHA_FLOOR
    if not alpha.any():
        return None
    luminance = 0.299 * block[:, :, 0] + 0.587 * block[:, :, 1] + 0.114 * block[:, :, 2]
    visible = luminance[alpha]
    low, high = float(visible.min()), float(visible.max())
    if high - low < 1e-6:
        steps = numpy.zeros(luminance.shape, dtype=numpy.uint8)
    else:
        steps = numpy.clip((luminance - low) / (high - low) * 3.999, 0, 3).astype(numpy.uint8)
    steps[~alpha] = 0
    outline = hashlib.sha1(numpy.packbits(alpha).tobytes()).hexdigest()
    return outline, hashlib.sha1(outline.encode() + steps.tobytes()).hexdigest()


def sheet_signature(art, sheet, relative):
    """tile -> shading_key, for one picture of one sheet. Filler is left out, so a sheet made
    mostly of blank squares cannot match another sheet made mostly of blank squares."""
    out = {}
    image = art.pixels(relative)
    if image is None:
        return out
    width = (art.index.get("artDim", {}).get(sheet) or [image.shape[1]])[0]
    columns = max(1, width // TILE)
    for row in range(image.shape[0] // TILE):
        for column in range(columns):
            block = image[row * TILE:(row + 1) * TILE, column * TILE:(column + 1) * TILE]
            if block.shape[:2] != (TILE, TILE) or is_filler(block):
                continue
            key = shading_key(block)
            if key:
                out[row * columns + column] = key
    return out


def recolour_copies(index, art, versions, labelled, placed, copies):
    """Labels carried onto the mod sheets that are a base-game sheet REPAINTED.

    Pixel matching finds art copied unchanged and stops there. A recolour changes every pixel, so
    nothing matches, and the sheet reads as new art when it is the base game's sheet in another
    palette - and a palette does not change what a tile IS. 797 placed tiles in the 22 August
    corpus are in that position.

    Three things have to hold at once, and each one alone is worthless:

      THE SHEETS MUST BE THE SAME SIZE and the mod's sheet must match the base game's outline on
      four fifths of the tiles it places. That is what earns the right to read tile N as the same
      slot in both. Without it this would be shape matching across the whole corpus, which
      contradicts itself 36% of the time.

      THE TILE'S OUTLINE MUST MATCH. A repaint does not move the edges of the drawing.

      THE SHADING ORDER MUST MATCH. This is the one that refuses. mine_frost_dark scores 83% of
      Island_Hut_tilesheet's outlines and agrees with its shading on zero tiles, which is the
      test saying those are two different pictures that happen to be cut the same way.

    And the guards the exact pass already keeps: only tiles a map places, never filler, and every
    version of the mod's sheet name must reach the same answer.

    One direction only. The base game is the source and the repaint is the destination, never the
    other way round, because the whole argument for this is that vanilla is the reference: paint
    the base game's sheet once and its repaints follow.
    """
    base = base_game_art(index)
    sources = {sheet: path for sheet, path in base.items() if labelled.get(sheet)}
    if not sources:
        return {}, []
    sizes = index.get("artDim") or {}
    representative = index.get("artPng") or {}
    signature_of = {sheet: sheet_signature(art, sheet, path) for sheet, path in sources.items()}

    pairs = []
    for sheet in sorted(placed):
        # A base-game sheet is never the DESTINATION. It is asked by name whether the name list
        # knows it or whether the no-mods pass proved it, because the first run of this paired
        # volcano_dungeon with volcano_dungeon and reported 152 of its own unpainted tiles as
        # work that painting the base game would unlock. One label file serves both, so that is
        # the same tiles counted twice.
        if sheet.lower() in VANILLA_SHEET_NAMES or sheet in base:
            continue
        drawn = placed[sheet]
        size = sizes.get(sheet)
        mine = representative.get(sheet)
        if not mine or not size or len(drawn) < RECOLOUR_MINIMUM_TILES:
            continue
        if not any(sizes.get(other) == size for other in sources):
            continue
        signature = {tile: key for tile, key in sheet_signature(art, sheet, mine).items()
                     if tile in drawn}
        if len(signature) < RECOLOUR_MINIMUM_TILES:
            continue
        best = None
        for other, theirs in signature_of.items():
            if sizes.get(other) != size:
                continue
            outlines = sum(1 for tile, key in signature.items()
                           if tile in theirs and theirs[tile][0] == key[0])
            share = outlines / len(signature)
            if share >= RECOLOUR_SHARE and (best is None or share > best[1]):
                best = (other, share)
        if best:
            pairs.append((sheet, best[0], best[1], len(signature)))

    carried, report = {}, []
    for sheet, vanilla, share, counted in pairs:
        answers = labelled[vanilla]
        known = labelled.get(sheet, {})
        taken = copies.get(sheet) or {}
        art_versions = sorted(versions.get(sheet, ()))
        moved = waiting = redrawn = 0
        for tile in sorted(placed[sheet]):
            if tile in known or tile in taken:
                continue
            base_block = art.block(sources[vanilla], vanilla, tile)
            if base_block is None or is_filler(base_block):
                continue
            wanted = shading_key(base_block)
            if wanted is None:
                continue
            agreed = bool(art_versions)
            for relative in art_versions:
                block = art.block(relative, sheet, tile)
                if block is None or is_filler(block) or shading_key(block) != wanted:
                    agreed = False
                    break
            if not agreed:
                redrawn += 1          # the repaint redrew this tile rather than recolouring it
                continue
            # The shading test is asked BEFORE the label is looked up, so a tile the base game
            # has not been painted yet is counted rather than skipped. That count is the whole
            # argument for painting vanilla first: it is the work this would finish by itself.
            raw = answers.get(tile)
            if raw is None:
                waiting += 1
                continue
            carried.setdefault(sheet, {})[tile] = raw
            moved += 1
        report.append((sheet, vanilla, share, counted, moved, waiting, redrawn))
    return carried, report


def silhouette_key(block):
    """The alpha shape, when there is a shape. A block that is fully opaque or fully empty
    matches everything with the same emptiness, which is how shape matching earns its bad name."""
    alpha = block[:, :, 3] > ALPHA_FLOOR
    empty = 1.0 - alpha.mean()
    if empty < 0.05 or empty > 0.95:
        return None
    return hashlib.sha1(numpy.packbits(alpha).tobytes()).hexdigest()


def choose(answers, labelled):
    """One label out of several given for the same picture, or None when nothing decides it.

    THE BASE GAME FIRST. If any of the disagreeing answers was painted on a vanilla sheet that is
    the one, because the base game is the reference every mod is a variation of and its sheets are
    the ones the whole corpus shares.

    THEN THE MOST-WORKED SHEET, where the picture is nowhere in the base game: the sheet carrying
    the most labelled tiles. Less a claim about which is right than about which was painted with
    the most of the rest of it in view, and the same tie-break the labeller's own twin button uses.

    THEN THE FULLER LABEL. The first two rules between them settled five disagreements out of
    9,223, because they both compare SHEETS and the commonest disagreement is a sheet with itself:
    one picture appearing at two tile indices on one sheet, painted once carefully and once in
    passing. Nothing about the sheet can separate those. The label covering more pixels can, and
    it is the same principle one step down - the more complete answer is the more considered one.

    Only a genuine tie survives, and those stay suggestions.

    Returns (label, why) so the report can say which rule spoke.
    """
    if len(answers) == 1:
        return next(iter(answers)), "only one answer"

    vanilla = {raw: where for raw, where in answers.items()
               if where[0].lower() in VANILLA_SHEET_NAMES}
    if len(vanilla) == 1:
        return next(iter(vanilla)), "the base game's own answer"

    pool = vanilla if len(vanilla) > 1 else answers
    from_base = pool is vanilla

    def painted_pixels(raw):
        return sum(1 for value in raw if value)

    ranked = sorted(pool.items(),
                    key=lambda kv: (-len(labelled.get(kv[1][0], {})), -painted_pixels(kv[0]),
                                    kv[1]))
    best, runner_up = ranked[0], ranked[1]
    best_sheet = len(labelled.get(best[1][0], {}))
    next_sheet = len(labelled.get(runner_up[1][0], {}))
    if best_sheet != next_sheet:
        return best[0], ("the most-worked base-game sheet" if from_base
                         else "the most-worked sheet")
    if painted_pixels(best[0]) != painted_pixels(runner_up[0]):
        return best[0], ("the fuller of the base game's answers" if from_base
                         else "the fuller label")
    return None, "tied"


def build(recolour=False):
    index = load_index()
    art = Art(index)
    versions = sheet_versions(index)
    files = label_files()

    placed = collections.defaultdict(set)
    for key, entry in index["locations"].items():
        sheets = entry.get("sheets") or []
        for cell in (entry.get("used") or []):
            if cell < 0:
                continue
            sheet_index, tile = divmod(cell, CELL_SHEET_STRIDE)
            if sheet_index < len(sheets):
                placed[sheets[sheet_index]].add(tile)
    placed = {s: t for s, t in placed.items()
              if not any(word in s.lower() for word in SKIP_SHEET)}

    labelled = {}
    unnamed = set()
    for sheet, path in files.items():
        document = read_labels(path)
        if not document:
            continue
        tiles = {}
        for tile, blob in (document.get("tiles") or {}).items():
            raw = base64.b64decode(blob)
            if len(raw) != TILE * TILE or not any(raw):
                continue
            # A byte outside the class list is not a class. 1,242 tiles in the corpus carry 255,
            # which is not one of the fifteen and is not the ground veto either (that lives in
            # its own store), so whatever it means it means something this does not know. Such a
            # tile is still a fine SOURCE of nothing and a bad thing to copy, so it is left where
            # it is rather than spread to tiles that do not have it yet.
            if max(raw) > len(CLASSES):
                unnamed.add((sheet, int(tile)))
                continue
            tiles[int(tile)] = raw
        if tiles:
            labelled[sheet] = tiles

    # every painted tile, indexed by exactly what it looks like
    # Keyed by what a tile looks like, holding every answer given for that picture AND the sheet
    # each came from. The source is what lets a disagreement be settled rather than only counted.
    by_pixels = collections.defaultdict(dict)
    by_shape = collections.defaultdict(dict)
    for sheet, tiles in labelled.items():
        for relative in versions.get(sheet, ()):
            for tile, raw in tiles.items():
                block = art.block(relative, sheet, tile)
                if block is None or not (block[:, :, 3] > ALPHA_FLOOR).any():
                    continue
                if is_filler(block):
                    continue
                by_pixels[exact_key(block)].setdefault(raw, (sheet, tile))
                shape = silhouette_key(block)
                if shape:
                    by_shape[shape].setdefault(raw, (sheet, tile))

    copies, conflicts, ambiguous, suggestions = {}, [], [], {}
    settled = {}
    for sheet, tiles in sorted(placed.items()):
        known = labelled.get(sheet, {})
        art_versions = sorted(versions.get(sheet, ()))
        for tile in sorted(tiles):
            if tile in known:
                continue
            blocks = [b for b in (art.block(v, sheet, tile) for v in art_versions) if b is not None]
            blocks = [b for b in blocks if (b[:, :, 3] > ALPHA_FLOOR).any()]
            if not blocks or any(is_filler(b) for b in blocks):
                continue
            # guards 2 and 3 together: ask every version of this tile what it is, and copy only
            # when they all answered and all answers are the same label byte for byte.
            answers = {}
            unanswered = False
            for block in blocks:
                found = by_pixels.get(exact_key(block))
                if found:
                    answers.update(found)
                else:
                    unanswered = True
            if answers and not unanswered and len(answers) == 1:
                copies.setdefault(sheet, {})[tile] = next(iter(answers))
                continue
            if answers and not unanswered and len(answers) > 1:
                picked, why = choose(answers, labelled)
                if picked is not None:
                    copies.setdefault(sheet, {})[tile] = picked
                    settled[why] = settled.get(why, 0) + 1
                    continue
            if len(answers) > 1:
                # The art is identical and the answers are not. Somebody painted this picture
                # twice and drew the edge differently, so no copy can be made - but it is the
                # strongest suggestion there is, far better than a shape match, because the
                # thing being labelled is the very same picture. Offered, ranked first.
                conflicts.append((sheet, tile, len(answers)))
                suggestions.setdefault(sheet, {})[tile] = {
                    "options": [base64.b64encode(a).decode() for a in sorted(answers)][:4],
                    "why": "same art, labelled more than one way",
                    "rank": 1}
                continue
            if answers and unanswered:
                ambiguous.append((sheet, tile))
                continue
            shape = silhouette_key(blocks[0])
            near = by_shape.get(shape) if shape else None
            if near:
                suggestions.setdefault(sheet, {})[tile] = {
                    "options": [base64.b64encode(a).decode() for a in sorted(near)][:4],
                    "why": "same outline as a labelled tile",
                    "rank": 2 if len(near) == 1 else 3}
    if unnamed:
        print("skipped %d labelled tile(s) carrying a class id that is not one of the %d classes"
              % (len(unnamed), len(CLASSES)))
    if settled:
        print("settled by rule: " + ", ".join("%s %d" % (k, v)
                                                for k, v in sorted(settled.items())))
    # Last, so an exact twin always beats a repaint: identical pixels are a stronger claim about
    # the same picture than identical shading is.
    repaints = []
    if recolour:
        carried, repaints = recolour_copies(index, art, versions, labelled, placed, copies)
        for sheet, tiles in carried.items():
            copies.setdefault(sheet, {}).update(tiles)
    return index, files, labelled, copies, conflicts, ambiguous, suggestions, repaints


def apply_copies(files, copies):
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = os.path.join(HFDIR, f"_labels-before-twins-{stamp}")
    os.makedirs(backup, exist_ok=True)
    written = 0
    for sheet, tiles in sorted(copies.items()):
        path = files.get(sheet) or os.path.join(HFDIR, "labels", f"{sheet}.labels.json")
        document = read_labels(path) if os.path.exists(path) else None
        if document is None:
            document = {"sheet": sheet, "format": "16x16-classes-base64", "tiles": {}}
        else:
            shutil.copy2(path, os.path.join(backup, os.path.basename(path)))
        document["classes"] = CLASSES
        document.setdefault("tiles", {})
        for tile, raw in tiles.items():
            document["tiles"][str(tile)] = base64.b64encode(raw).decode()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with io.open(path, "w", encoding="utf-8") as handle:
            json.dump(document, handle, ensure_ascii=False)
        written += len(tiles)
    return written, backup


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="write the exact twins")
    parser.add_argument("--suggest", action="store_true", help="write the near misses too")
    parser.add_argument("--recolour", action="store_true",
                        help="also carry labels onto the sheets that are a base-game sheet repainted")
    arguments = parser.parse_args()

    index, files, labelled, copies, conflicts, ambiguous, suggestions, repaints = \
        build(recolour=arguments.recolour)
    total_copies = sum(len(t) for t in copies.values())
    total_suggest = sum(len(t) for t in suggestions.values())
    painted = sum(len(t) for t in labelled.values())

    print(f"{painted:,} tiles already labelled on {len(labelled)} sheets")
    print(f"exact twins ready to copy : {total_copies:,} on {len(copies)} sheets")
    print(f"  twins that disagree     : {len(conflicts):,} (left alone)")
    print(f"  only some versions known: {len(ambiguous):,} (one name, and not every picture of it is labelled)")
    ranks = collections.Counter(entry["rank"] for tiles in suggestions.values()
                                for entry in tiles.values())
    print(f"suggestions for a person  : {total_suggest:,} on {len(suggestions)} sheets "
          f"(same art {ranks[1]:,}, one shape match {ranks[2]:,}, several {ranks[3]:,})")
    for sheet, tiles in sorted(copies.items(), key=lambda kv: -len(kv[1]))[:12]:
        print(f"   {len(tiles):>5}  {sheet}")

    if arguments.recolour:
        carried = sum(row[4] for row in repaints)
        waiting = sum(row[5] for row in repaints)
        redrawn = sum(row[6] for row in repaints)
        print()
        print(f"sheets that are a base-game sheet repainted: {len(repaints)}")
        print(f"  labels carried across the repaint        : {carried:,}")
        print(f"  tiles waiting on the base game's own tile: {waiting:,} "
              f"(painting vanilla finishes these by itself)")
        print(f"  tiles the repaint genuinely redrew       : {redrawn:,} (nobody can carry those)")
        for sheet, vanilla, share, counted, moved, held, _ in \
                sorted(repaints, key=lambda r: -(r[4] + r[5]))[:14]:
            if not (moved + held):
                continue
            print(f"   {moved:>4} now {held:>4} waiting  {sheet[:34]:<34} <- {vanilla} "
                  f"({share * 100:.0f}% of {counted})")

    if arguments.suggest:
        path = os.path.join(HFDIR, "twin-suggestions.json")
        with io.open(path, "w", encoding="utf-8") as handle:
            json.dump({"//": "Tiles this could not label on its own, for the labeller to offer one "
                             "at a time. rank 1 is the same picture labelled more than one way, "
                             "which is a choice between real answers; rank 2 and 3 match only the "
                             "outline, and a third of those contradict each other, so they are a "
                             "hint and never an answer.",
                       "sheets": suggestions}, handle, ensure_ascii=False)
        print(f"wrote {path}")
    if arguments.apply:
        if not total_copies:
            print("nothing to copy")
            return
        written, backup = apply_copies(files, copies)
        print(f"wrote {written:,} labels; the files they replaced are in {backup}")


if __name__ == "__main__":
    main()
