"""The window glass labeler: a small local server and a page to check and paint window glass.

    python tools/windowglass/labeler.py E:\\Games\\GamesMods\\DevStardew\\_window-corpus [--port 8765]

then open http://127.0.0.1:8765. Reads every <name>-windows.jsonl the corpus review wrote
(.snapshot-smapi/windowcorpus/review.py), one row per distinct window picture with the glass found
automatically, and serves them grouped by mod. Every change is written at once to
<corpus>/glass-labels/<digest>.json, one small file per window, so nothing lives only in the
browser and a closed tab loses nothing.

A label's status says what happened to the automatic answer, which is how its mistakes are counted:
  auto-ok   the automatic glass was right as it was
  edited    it was wrong and was painted over
  no-glass  the picture has no glass that lets light in
tools/labelops/windowglass.py prefers these labels over the automatic glass when it writes
labels/window-glass.json.
"""
import argparse, io, json, os, re, sys, time, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
import numpy
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, "labeler.html")
# "draft" is paint kept but not confirmed: the window counts as still to check.
# "copy-unchecked" is a twin's glass given to this window, waiting for the user to look at it:
# like a draft it is still to check (user, 16 Oct 2026: "even copied, do not confirm, let me check").
STATUSES = ("auto-ok", "edited", "no-glass", "copied", "draft", "copy-unchecked")
UNJUDGED = ("draft", "copy-unchecked")
# Two windows are the same shape when their outlines match exactly and their frames (the pixels in
# the colours of the picture's own outer edge) overlap this much: a recolour of one window, or the
# same window drawn again by another pack.
SAME_SHAPE = 0.85
SAME_LINES = 0.5
SAME_OUTLINE = 0.96
# Lines that agree this closely carry a weaker frame score: a seasonal recolour mixes the frame's
# colours into the glass, its frame score drops to 0.75 while its lines stay at 0.91 (Japanese
# seasonal windows, 16 Oct 2026).
STRONG_LINES = 0.85
STRONG_LINES_FRAME = 0.70
# A window still to check is shown with the glass of a labelled twin this alike, instead of the
# automatic answer: what the user decided for one window is offered to the windows that match it.
SUGGEST_ALIKE = 0.90
# "No glass" is only offered for a picture practically the same.
SUGGEST_NO_GLASS_ALIKE = 0.98
# A judged window's answer is given outright (status "copied") to twins this alike that nobody has
# judged: a recolour of a window is the same window. The user asked for it, 16 Oct 2026.
APPLY_ALIKE = 0.90
# and their lines must agree more than a twin's need to (a recoloured porthole: 0.79)
APPLY_LINES = 0.6
# Carrying a hand fix to the same mistake elsewhere (Corpus.carry_fix): two fixes are the same
# when they changed this share of the same pixels, and a window still has the mistake when this
# share of the fixed pixels is still as it was.
FIX_SAME_PIXELS = 0.7
FIX_STILL_WRONG = 0.8
# Fitting a copied glass to the window's own picture (Corpus._fit_to_picture): a pixel differs
# when it is this far from the source's colour carried over (0-255, the largest channel), counts
# when this many of the 3x3 around it differ too, and the copy is left alone when more than this
# share of its glass differs (the glass art itself is another design).
FIT_COLOUR_GAP = 48
FIT_CLUMP = 4
FIT_MOST_DIFFERS = 0.6
# ...and a pixel changes at night when its pair picture differs by more than this (largest channel).
FIT_NIGHT_CHANGE = 24


CONTENT_PATCHER_TOKEN = re.compile(r"\{\{[^}]*\}\}[_.\-\s]*")


def readable_name(name):
    """A name as a mod's files spell it, without Content Patcher tokens: "{{ModID}}_Th3FishBlueWindow"
    is the pack's own prefix plus the item's name, and only the name is worth reading."""
    if not name:
        return name
    cleaned = CONTENT_PATCHER_TOKEN.sub("", str(name)).strip(" _.-")
    return cleaned or str(name)


def has_bits(hex_text):
    return bool(hex_text) and any(character not in "0" for character in hex_text)


def unpack(hex_text, height, width):
    bits = numpy.unpackbits(numpy.frombuffer(bytes.fromhex(hex_text or ""), numpy.uint8))
    out = numpy.zeros(height * width, bool)
    out[:min(len(bits), height * width)] = bits[:height * width].astype(bool)
    return out.reshape(height, width)


class Corpus:
    def __init__(self, root):
        self.root = root
        self.review = os.path.join(root, "review")
        self.labels = os.path.join(root, "glass-labels")
        os.makedirs(self.labels, exist_ok=True)
        self.lock = threading.Lock()
        self.names = self._mod_names()
        self.windows, self.by_mod = self._load_windows()
        self.twins = self._same_shapes()
        # Every label file read once; saves and clears keep this in step.
        self._labels = {}
        # Live updates: windows written since the last publish, and the events sent to the pages.
        self.dirty = set()
        self.events = []
        self.version = 0
        self.changed = threading.Condition()
        self._tile_index = None
        self._content_cache = {}
        self._night_cache = {}
        for file in os.listdir(self.labels):
            if file.endswith(".json"):
                try:
                    record = json.load(open(os.path.join(self.labels, file), encoding="utf-8"))
                    self._labels[record["digest"]] = record
                except Exception:
                    pass

    def _mod_names(self):
        names = {"vanilla": "Stardew Valley (the game)", "sve": "Stardew Valley Expanded"}
        for file in ("candidates.json",):
            path = os.path.join(self.root, file)
            if os.path.exists(path):
                for mod in json.load(open(path, encoding="utf-8")):
                    names[str(mod["modId"])] = mod["name"]
        return names

    def _load_windows(self):
        windows, by_mod = {}, {}
        for file in sorted(os.listdir(self.review)):
            # The unprefixed windows.jsonl is the first run, from rules since replaced.
            if not file.endswith("-windows.jsonl"):
                continue
            for line in open(os.path.join(self.review, file), encoding="utf-8"):
                row = json.loads(line)
                digest = row.get("digest")
                if not digest or digest in windows or row.get("kind") == "floor-sunlight":
                    continue
                windows[digest] = row
                by_mod.setdefault(str(row["mod"]), []).append(digest)
        return windows, by_mod

    def _crop(self, path, rect, cache):
        if path not in cache:
            cache[path] = numpy.array(Image.open(path).convert("RGBA"))
        x, y, w, h = rect
        return cache[path][y:y + h, x:x + w]

    def _same_shapes(self):
        """For each window, the others of the same shape, with how alike their frames are."""
        cache, by_outline, frames = {}, {}, {}
        self._edge_cache = {}
        self.art = {}
        for digest, row in self.windows.items():
            art = self._crop(row["picture"], row["rect"], cache).copy()
            self.art[digest] = art
            opaque = art[..., 3] > 0
            # grouped by size; within a size the outlines are compared, not required to be equal
            by_outline.setdefault(art.shape, []).append(digest)
            ring = opaque & ~numpy.pad(opaque, 1)[2:, 1:-1] | opaque & ~numpy.pad(opaque, 1)[:-2, 1:-1]                    | opaque & ~numpy.pad(opaque, 1)[1:-1, 2:] | opaque & ~numpy.pad(opaque, 1)[1:-1, :-2]
            rgb = art[..., :3].astype(int)
            palette = numpy.unique(rgb[ring], axis=0) if ring.any() else numpy.zeros((0, 3), int)
            if len(palette):
                nearest = numpy.min(numpy.abs(rgb.reshape(-1, 1, 3) - palette[None]).sum(-1), axis=1).reshape(opaque.shape)
                frames[digest] = opaque & (nearest <= 30)
            else:
                frames[digest] = numpy.zeros_like(opaque)
        # A picture with no frame to speak of (a plain filled rectangle: a view, a curtain, a wall)
        # matches every other plain rectangle by outline and frame alike; its shape says nothing.
        # Such windows are matched only by identical tiles, never as twins.
        def has_frame(digest):
            opaque = self.art[digest][..., 3] > 0
            share = frames[digest].sum() / max(1, opaque.sum())
            return 0.03 <= share <= 0.85
        self.frameless = {digest for digest in self.windows if not has_frame(digest)}
        twins = {digest: [] for digest in self.windows}
        for group in by_outline.values():
            group = [digest for digest in group if digest not in self.frameless]
            opaque = {digest: self.art[digest][..., 3] > 0 for digest in group}
            for i, first in enumerate(group):
                for second in group[i + 1:]:
                    # The outlines nearly the same: a recolour sometimes moves a few edge pixels
                    # (a porthole six pixels off of about 250, 16 Oct 2026).
                    outline_union = (opaque[first] | opaque[second]).sum()
                    if not outline_union or (opaque[first] & opaque[second]).sum() / outline_union < SAME_OUTLINE:
                        continue
                    union = (frames[first] | frames[second]).sum()
                    alike = float((frames[first] & frames[second]).sum() / union) if union else 1.0
                    # and the lines of the picture fall in the same places: a recolour keeps them
                    # (0.69 and up on the copies of 16 Oct 2026), two different pictures in the same
                    # plain rectangle do not (0.32 to 0.38)
                    lines = self._lines_alike(first, second)
                    if (alike >= SAME_SHAPE and lines >= SAME_LINES) or (lines >= STRONG_LINES and alike >= STRONG_LINES_FRAME):
                        twins[first].append((second, alike))
                        twins[second].append((first, alike))
        for digest in twins:
            twins[digest].sort(key=lambda pair: -pair[1])
        self.twin_alike = {(first, second): alike for first, pairs in twins.items() for second, alike in pairs}
        return twins

    def _edges(self, digest):
        if digest not in self._edge_cache:
            art = self.art[digest].astype(int)
            rgb, opaque = art[..., :3], art[..., 3] > 0
            edges = numpy.zeros(opaque.shape, bool)
            edges[:, :-1] |= (numpy.abs(rgb[:, 1:] - rgb[:, :-1]).sum(-1) > 60) | (opaque[:, 1:] != opaque[:, :-1])
            edges[:-1, :] |= (numpy.abs(rgb[1:] - rgb[:-1]).sum(-1) > 60) | (opaque[1:] != opaque[:-1])
            self._edge_cache[digest] = edges
        return self._edge_cache[digest]

    def _lines_alike(self, first, second):
        a, b = self._edges(first), self._edges(second)
        if a.shape != b.shape:      # a base found by identical tiles can be another size
            return 0.0
        union = (a | b).sum()
        return float((a & b).sum() / union) if union else 1.0

    def label(self, digest):
        return self._labels.get(digest)

    def _tiles_judged(self):
        """Each 16x16 piece of art in a judged window, by its exact pixels, with the glass the user
        gave it. A window built from the same pieces (two panes side by side, a pack drawing the
        same tiles into another layout) is then answered piece by piece."""
        if self._tile_index is None:
            index = {}
            for digest, label in self._labels.items():
                if label["status"] in UNJUDGED or digest not in self.art:
                    continue
                art = self.art[digest]
                height, width = art.shape[:2]
                mask = unpack(label["mask"], height, width)
                for top in range(0, height - 15, 16):
                    for left in range(0, width - 15, 16):
                        tile = art[top:top + 16, left:left + 16]
                        if not (tile[..., 3] > 0).any():
                            continue
                        key = tile.tobytes()
                        stamp = label.get("savedAt", "")
                        if key not in index or stamp > index[key][1]:
                            index[key] = (mask[top:top + 16, left:left + 16].copy(), stamp, digest)
            self._tile_index = index
        return self._tile_index

    def _suggestion_by_tiles(self, digest):
        art = self.art.get(digest)
        if art is None:
            return None
        height, width = art.shape[:2]
        index = self._tiles_judged()
        row = self.windows[digest]
        mask = unpack(row.get("mask"), height, width) if row.get("mask") else numpy.zeros((height, width), bool)
        found = needed = 0
        sources = []
        for top in range(0, height - 15, 16):
            for left in range(0, width - 15, 16):
                tile = art[top:top + 16, left:left + 16]
                if not (tile[..., 3] > 0).any():
                    continue
                needed += 1
                hit = index.get(tile.tobytes())
                if hit:
                    found += 1
                    mask[top:top + 16, left:left + 16] = hit[0]
                    sources.append(hit[2])
        if not found or (height % 16 or width % 16):
            return None
        source = max(set(sources), key=sources.count)
        source_row = self.windows[source]
        return {"from": source, "alike": round(found / needed, 2), "status": "copied",
                "mask": numpy.packbits(mask.reshape(-1)).tobytes().hex(),
                "name": readable_name(source_row.get("name")), "modName": self.names.get(str(source_row["mod"]), str(source_row["mod"])),
                "tiles": [found, needed]}

    def suggestion(self, digest):
        """Glass from the user's own answers: the same pieces of art judged before, or else a
        labelled twin of this window's shape."""
        by_tiles = self._suggestion_by_tiles(digest)
        if by_tiles and by_tiles["tiles"][0] == by_tiles["tiles"][1]:
            return by_tiles
        best = None
        for twin, alike in self.twins.get(digest, []):
            label = self._labels.get(twin)
            if not label or label["status"] in UNJUDGED or not self.close_enough(digest, twin, alike, SUGGEST_ALIKE, SAME_LINES):
                continue
            if label["status"] == "no-glass" and alike < SUGGEST_NO_GLASS_ALIKE:
                continue
            rank = (round(self.closeness(digest, twin), 3), label["status"] != "copied", label.get("savedAt", ""))
            if best is None or rank > best[0]:
                best = (rank, twin, alike, label)
        if not best:
            return by_tiles
        _, twin, alike, label = best
        row = self.windows[twin]
        return {"from": twin, "alike": round(alike, 2), "status": label["status"], "mask": self.fitted(label["mask"], digest, twin),
                "sheer": self.fitted(label["sheer"], digest, twin) if has_bits(label.get("sheer")) else "",
                "name": readable_name(row.get("name")), "modName": self.names.get(str(row["mod"]), str(row["mod"]))}

    def fitted(self, mask_hex, digest, source=None):
        """A glass copied from a twin, trimmed to this window's own outline, and, given the window
        it came from, to this window's own picture (_fit_to_picture)."""
        art = self.art[digest]
        height, width = art.shape[:2]
        mask = unpack(mask_hex, height, width) & (art[..., 3] > 0)
        if source and source != digest and source in self.art and self.art[source].shape == art.shape:
            mask = self._fit_to_picture(mask, source, digest)
        return numpy.packbits(mask.reshape(-1)).tobytes().hex()

    @staticmethod
    def _colour_keys(art):
        return (art[..., 0].astype(numpy.uint32) << 24) | (art[..., 1].astype(numpy.uint32) << 16)             | (art[..., 2].astype(numpy.uint32) << 8) | art[..., 3].astype(numpy.uint32)

    def _something_in_front(self, source, digest):
        """
        Where this window shows something the twin it copies from does not, pixel by pixel.

        The source's colours are first carried over to this window's (a straight line per
        channel, fitted over every pixel both show), so a recolour, a season or a palette swap
        reads as the same picture. What still differs by more than FIT_COLOUR_GAP, in a clump
        rather than a speck of noise, is something else standing there: an item, a plant.
        """
        key = (source, digest)
        if key not in self._content_cache:
            source_art = self.art[source][..., :3].astype(float)
            target_art = self.art[digest][..., :3].astype(float)
            both = (self.art[source][..., 3] > 0) & (self.art[digest][..., 3] > 0)
            carried = numpy.zeros_like(source_art)
            for channel in range(3):
                x, y = source_art[..., channel][both], target_art[..., channel][both]
                spread = x.var()
                slope = ((x - x.mean()) * (y - y.mean())).mean() / spread if spread > 1e-6 else 0.0
                carried[..., channel] = y.mean() + slope * (source_art[..., channel] - x.mean())
            differs = both & (numpy.abs(carried - target_art).max(axis=2) > FIT_COLOUR_GAP)
            padded = numpy.pad(differs, 1)
            around = sum(padded[1 + dy:padded.shape[0] - 1 + dy, 1 + dx:padded.shape[1] - 1 + dx]
                         for dy in (-1, 0, 1) for dx in (-1, 0, 1))
            self._content_cache[key] = differs & (around >= FIT_CLUMP)
        return self._content_cache[key]

    def _changes_at_night(self, digest):
        """The pixels of this window that change in its pair picture (night or rain), or None
        when it has none: glass changes, a bottle or a plant in front of it does not."""
        if digest not in self._night_cache:
            row = self.windows[digest]
            changes = None
            if row.get("pair") and row.get("pairPicture") and os.path.exists(row["pairPicture"]):
                try:
                    with Image.open(row["pairPicture"]) as picture:
                        x, y, w, h = row["pair"]
                        night = numpy.array(picture.convert("RGBA").crop((x, y, x + w, y + h))).astype(int)
                    day = self.art[digest].astype(int)
                    if night.shape == day.shape:
                        changes = numpy.abs(night - day).max(axis=2) > FIT_NIGHT_CHANGE
                except OSError:
                    changes = None
            self._night_cache[digest] = changes
        return self._night_cache[digest]

    def _fit_to_picture(self, mask, source, digest):
        """
        A twin's glass laid on this window's own picture (user, 16 Oct 2026: two windows of one
        frame, one with a shelf of things in front of the glass and one with a plant, were given
        the same glass from one base).

        Where something stands in front of the glass here and not in the source, the glass goes.
        Where the source had something in front and this window shows a colour its own glass has,
        inside the glass's bounds, the glass is added. When most of the glass differs, it is the
        glass art itself that is another design, not a thing in front of it, and the copy is left
        as it was.
        """
        if not mask.any():
            return mask
        in_front = self._something_in_front(source, digest)
        # With a night picture, a pixel that changes at night is glass whatever the day shows
        # (a highlight, a sparkle painted on the pane); only what stays the same is in front.
        night = self._changes_at_night(digest)
        if night is not None:
            in_front = in_front & ~night
        if (mask & in_front).sum() > FIT_MOST_DIFFERS * mask.sum():
            return mask
        kept = mask & ~in_front
        if not kept.any():
            return mask
        target_keys = self._colour_keys(self.art[digest])
        colours, counts = numpy.unique(target_keys[kept], return_counts=True)
        glass_colours = colours[counts >= 2]
        rows, columns = numpy.where(mask)
        inside = numpy.zeros_like(mask)
        inside[rows.min():rows.max() + 1, columns.min():columns.max() + 1] = True
        differs = self._something_in_front(source, digest)
        uncovered = ~mask & differs & inside & (self.art[digest][..., 3] > 0)
        # with a night picture, what changes at night is glass, whatever shade of it the day shows
        uncovered &= night if night is not None else numpy.isin(target_keys, glass_colours)
        return kept | uncovered

    @staticmethod
    def _values(mask_hex, sheer_hex, height, width):
        """A window's picture as 0 clear, 1 glass, 2 sheer curtain, pixel by pixel."""
        values = numpy.zeros((height, width), numpy.uint8)
        if has_bits(sheer_hex):
            values[unpack(sheer_hex, height, width)] = 2
        if has_bits(mask_hex):
            values[unpack(mask_hex, height, width)] = 1
        return values

    def picture_of(self, digest):
        """What the page shows for a window: its label, or else the glass suggested from the
        user's answers, or else the automatic glass."""
        height, width = self.art[digest].shape[:2]
        source = self._labels.get(digest) or self.suggestion(digest) or {"mask": self.windows[digest].get("mask") or ""}
        return self._values(source.get("mask"), source.get("sheer"), height, width)

    @staticmethod
    def _same_fix(first, second):
        """Two hand fixes that changed nearly the same pixels the same way."""
        first = {index: (before, after) for index, before, after in first["pixels"]}
        second = {index: (before, after) for index, before, after in second["pixels"]}
        both = first.keys() & second.keys()
        either = first.keys() | second.keys()
        if not either or len(both) < FIX_SAME_PIXELS * len(either):
            return False
        return sum(first[index] == second[index] for index in both) >= 0.9 * len(both)

    def carry_fix(self, digest):
        """
        Give a hand fix to the other windows of the mod that hold the same mistake (user, 16 Oct
        2026: the first windows of a mod were wrong, their copies spread the mistake, and the
        same fix had to be painted on one window after another).

        A window of the same outline is given it when its picture is exactly what this window
        showed before the fix, or, once the same fix has been made on another window too, when
        most of the pixels it changed are still as they were here. Only the changed pixels move,
        so each window keeps its own glass elsewhere. They wait for a check, and keep what they
        had in "before" so taking the fix back restores them.
        """
        label = self._labels.get(digest)
        fix = label and label.get("fix")
        if not fix or digest not in self.art:
            return []
        art = self.art[digest]
        height, width = art.shape[:2]
        outline = art[..., 3] > 0
        mod = str(self.windows[digest]["mod"])
        repeats = [other for other in self.by_mod.get(mod, [])
                   if other != digest and (self._labels.get(other) or {}).get("fix")
                   and self._labels[other]["maskShape"] == label["maskShape"]
                   and self._same_fix(fix, self._labels[other]["fix"])]
        started = self._values(fix["from"].get("mask"), fix["from"].get("sheer"), height, width)
        flat_indices = numpy.array([index for index, _, _ in fix["pixels"]])
        befores = numpy.array([before for _, before, _ in fix["pixels"]], numpy.uint8)
        afters = numpy.array([after for _, _, after in fix["pixels"]], numpy.uint8)
        changed = []
        for other in self.by_mod.get(mod, []):
            if other == digest or other not in self.art or self.art[other].shape[:2] != (height, width):
                continue
            # Never back onto the window this one was copied from: its glass was right for its own
            # picture, and this fix is about this window's (16 Oct 2026: fixing the bottle shelf
            # rewrote the plant window it had been copied from).
            if other == label.get("copiedFrom"):
                continue
            current_label = self._labels.get(other)
            # mid-painting, or said to have no glass: not touched
            if current_label and current_label["status"] in ("draft", "no-glass"):
                continue
            other_outline = self.art[other][..., 3] > 0
            union = (outline | other_outline).sum()
            if not union or (outline & other_outline).sum() / union < SAME_OUTLINE:
                continue
            # The fix answers what this window shows at those pixels; a window that shows something
            # else there (a plant where this one has bottles) is not making the same mistake, even
            # when it holds the same glass.
            shows_the_same = ~self._something_in_front(digest, other).reshape(-1)[flat_indices]
            if shows_the_same.mean() < FIX_STILL_WRONG:
                continue
            current = self.picture_of(other)
            here = current.reshape(-1)[flat_indices]
            still_wrong = here == befores
            exact = bool((current == started).all())
            if not (exact or (repeats and still_wrong.mean() >= FIX_STILL_WRONG)):
                continue
            fixed = current.reshape(-1).copy()
            fixed[flat_indices[still_wrong]] = afters[still_wrong]
            fixed = fixed.reshape(height, width)
            fixed[~other_outline] = 0
            if (fixed == current).all():
                continue
            # A window painted by hand keeps its glass; it is marked to look at again instead.
            if current_label and current_label["status"] == "edited":
                if current_label.get("recheck") != digest:
                    self.mark_recheck(other, digest)
                    changed.append(other)
                continue
            covered = current_label.get("before") if current_label and current_label["status"] == "copy-unchecked" else (
                {"status": current_label["status"], "mask": current_label["mask"], "sheer": current_label.get("sheer") or "",
                 "copiedFrom": current_label.get("copiedFrom")}
                if current_label else None)
            self.save(other, "copy-unchecked", numpy.packbits((fixed == 1).reshape(-1)).tobytes().hex(), copied_from=digest,
                      spread=False, before=covered, extra={"carriedFix": True, "sameFix": len(repeats) + 1},
                      sheer_hex=numpy.packbits((fixed == 2).reshape(-1)).tobytes().hex())
            changed.append(other)
        return changed

    def lift_rechecks_from(self, digest):
        """Take away the "check again" marks this window's fix had put on others."""
        for other, held in list(self._labels.items()):
            if held.get("recheck") == digest:
                record = {key: value for key, value in held.items() if key != "recheck"}
                path = os.path.join(self.labels, other + ".json")
                with self.lock:
                    with open(path + ".tmp", "w", encoding="utf-8") as file:
                        json.dump(record, file)
                    os.replace(path + ".tmp", path)
                    self._labels[other] = record
                    self.dirty.add(other)

    def mark_recheck(self, digest, because):
        """Flag a judged window to be looked at again; its verdict and glass stay."""
        label = self._labels.get(digest)
        if not label:
            return
        record = {**label, "recheck": because}
        path = os.path.join(self.labels, digest + ".json")
        with self.lock:
            with open(path + ".tmp", "w", encoding="utf-8") as file:
                json.dump(record, file)
            os.replace(path + ".tmp", path)
            self._labels[digest] = record
            self.dirty.add(digest)

    def close_enough(self, first, second, alike, frame_bar, lines_bar):
        """A twin near enough to give or suggest its glass: a frame and lines both past their bars,
        or lines in very close agreement with a frame past the lower bar."""
        lines = self._lines_alike(first, second)
        return (alike >= frame_bar and lines >= lines_bar) or (lines >= STRONG_LINES and alike >= STRONG_LINES_FRAME)

    def closeness(self, first, second):
        """How close two windows are, frames and lines together: which base a window follows."""
        return (self.twin_alike.get((first, second), 0.0) + self._lines_alike(first, second)) / 2

    def consensus(self, digest, fallback_hex):
        """The glass for a window from every judged twin close enough to give it, by a vote
        weighted by closeness, pixel by pixel (user, 16 Oct 2026: copy from several at once, to be
        sure). Returns the glass and how the bases voted."""
        bases = []
        for twin, alike in self.twins.get(digest, []):
            label = self._labels.get(twin)
            if not label or label["status"] not in ("auto-ok", "edited", "no-glass", "copied"):
                continue
            if not self.close_enough(digest, twin, alike, APPLY_ALIKE, APPLY_LINES):
                continue
            bases.append((twin, self.closeness(digest, twin), self.fitted(label["mask"], digest, twin)))
        if len(bases) < 2:
            return fallback_hex, ({"agree": 1, "total": 1, "sources": [b[0] for b in bases]} if bases else None)
        height, width = self.art[digest].shape[:2]
        weight_sum = sum(weight for _, weight, _ in bases)
        tally = numpy.zeros((height, width))
        for _, weight, mask_hex in bases:
            tally += weight * unpack(mask_hex, height, width)
        mask = tally >= weight_sum / 2
        mask_hex = numpy.packbits(mask.reshape(-1)).tobytes().hex()
        agree = sum(1 for _, _, base_hex in bases if base_hex == mask_hex)
        return mask_hex, {"agree": agree, "total": len(bases), "sources": [b[0] for b in sorted(bases, key=lambda b: -b[1])]}

    def apply_to_twins(self, digest):
        """Give this window's label to its close twins still to check, and to the twins that hold an
        earlier copy of it. Returns the windows changed."""
        label = self._labels.get(digest)
        if not label or label["status"] in UNJUDGED:
            return []
        changed = []
        targets = {twin for twin, alike in self.twins.get(digest, [])
                   if self.close_enough(digest, twin, alike, APPLY_ALIKE, APPLY_LINES)}
        # every window that holds a copy of this one follows it, wherever the copy came from
        # (not a carried fix: that copy holds only the fixed pixels, over its own glass)
        targets |= {other for other, held in self._labels.items()
                    if other != digest and held.get("copiedFrom") == digest and held["status"] in ("copy-unchecked", "copied")
                    and not held.get("carriedFix")}
        by_tiles = set()
        for other in self.windows:
            if other != digest and other not in targets and not self._labels.get(other):
                suggestion = self._suggestion_by_tiles(other)
                if suggestion and suggestion["from"] == digest and suggestion["tiles"][0] == suggestion["tiles"][1]:
                    by_tiles.add(other)
        for twin in targets | by_tiles:
            # never back onto the base this window was copied from: a fixed copy marks its base
            # to check again, it does not overwrite it
            if twin == label.get("copiedFrom"):
                continue
            current = self._labels.get(twin)
            # Windows nobody judged, and copies of this same source, are given it. A copy the user
            # had confirmed goes back to waiting for a check when its source changes: it was
            # confirmed against the old glass (user, 16 Oct 2026). When the source was fixed by
            # hand, its twins the user had passed as "auto right" get the fix to check too: the
            # automatic answer was wrong for the source, so it is likely wrong for them. A twin the
            # user painted is marked to look at again instead of being overwritten.
            fixed_by_hand = label["status"] == "edited"
            if current and current.get("carriedFix"):
                continue
            is_copy_of_this = current and current["status"] in ("copy-unchecked", "copied") and current.get("copiedFrom") == digest
            # An unchecked copy of another base moves to this one when it is closer to this one:
            # a fixed copy becomes a base of its own, and each window follows the base it is
            # nearest (user, 16 Oct 2026).
            closer_base = (current and current["status"] == "copy-unchecked" and current.get("copiedFrom") not in (None, digest)
                           and current["copiedFrom"] in self._labels
                           and self.closeness(twin, digest) > self.closeness(twin, current["copiedFrom"]))
            # A judged window is only overwritten by a fix from its own mod: one fix in Furniture Redone
            # rewrote 44 judged windows of Fruit Smoothie (16 Oct 2026). Other mods keep their verdicts.
            same_mod = str(self.windows[twin]["mod"]) == str(self.windows[digest]["mod"])
            passed_as_auto = current and current["status"] == "auto-ok" and fixed_by_hand and twin in targets and same_mod
            if current and current["status"] == "edited" and fixed_by_hand and twin in targets and same_mod:
                if current["mask"] != self.fitted(label["mask"], twin, digest) and current.get("recheck") != digest:
                    self.mark_recheck(twin, digest)
                    changed.append(twin)
                continue
            if current and not (is_copy_of_this or passed_as_auto or closer_base):
                continue
            votes = None
            if twin in by_tiles:
                mask = self._suggestion_by_tiles(twin)["mask"]
            else:
                voted, votes = self.consensus(twin, self.fitted(label["mask"], twin, digest))
                # A fresh hand fix is carried as it is, the vote only tells how many bases agree
                # with it; otherwise the vote decides (a fix outvoted by twelve "auto right" twins
                # would never reach anyone).
                mask = self.fitted(label["mask"], twin, digest) if fixed_by_hand else voted
                if votes and fixed_by_hand:
                    votes = {**votes, "agree": sum(1 for source in votes["sources"]
                                                   if self.fitted(self._labels[source]["mask"], twin, source) == mask)}
            status = "copy-unchecked"
            sheer = self.fitted(label["sheer"], twin, digest) if has_bits(label.get("sheer")) else ""
            if not has_bits(sheer):
                sheer = ""
            if current and current["mask"] == mask and (current.get("sheer") or "") == sheer:
                continue
            covered = current.get("before") if current and current["status"] == "copy-unchecked" else (
                {"status": current["status"], "mask": current["mask"], "sheer": current.get("sheer") or "",
                 "copiedFrom": current.get("copiedFrom")} if current else None)
            # the source has come back to what this window had: give it back as it was
            if covered and covered["mask"] == mask and (covered.get("sheer") or "") == sheer:
                self.restore(twin, covered)
                changed.append(twin)
                continue
            self.save(twin, status, mask, copied_from=digest, spread=False, before=covered,
                      extra={"votes": votes} if votes else None, sheer_hex=sheer)
            changed.append(twin)
        return changed

    def restore(self, digest, covered):
        """Put back what a window held before a copy was laid over it, where it came from included.
        Restoring through a plain save kept the copy's source in the file, and with it the copy's
        marks."""
        self.save(digest, covered["status"], covered["mask"], copied_from=covered.get("copiedFrom") or "", spread=False,
                  sheer_hex=covered.get("sheer"), restoring=True)

    def save(self, digest, status, mask_hex, copied_from=None, spread=True, before=None, extra=None, sheer_hex=None, restoring=False):
        row = self.windows[digest]
        previous = None if restoring else self._labels.get(digest)
        copied_from = copied_from or None if restoring else copied_from
        # a copy keeps where it came from when it is confirmed as it is
        if copied_from is None and previous and previous.get("copiedFrom") and status in ("copied", "no-glass", "copy-unchecked", "edited", "draft"):
            copied_from = previous["copiedFrom"]
        record = {"digest": digest, "status": status, "mask": mask_hex, "maskShape": row["maskShape"],
                  "mod": row["mod"], "name": row.get("name"), "picture": row["picture"], "rect": row["rect"],
                  "pair": row.get("pair"), "pairPicture": row.get("pairPicture"), "copies": row.get("copies", []),
                  "autoMask": row.get("mask"), "copiedFrom": copied_from, "savedAt": time.strftime("%Y-%m-%d %H:%M:%S")}
        if has_bits(sheer_hex):
            record["sheer"] = sheer_hex
        if before:
            record["before"] = before
        # a carried fix stays one while it keeps its source (see carry_fix and apply_to_twins)
        if previous and previous.get("carriedFix") and copied_from == previous.get("copiedFrom"):
            record["carriedFix"] = True
        # What the hand work started from, kept while it is a draft, so the fix it ends in can be
        # told apart and carried to the same mistake elsewhere (carry_fix).
        if status in ("draft", "edited") and digest in self.art and not restoring:
            if previous and previous["status"] == "draft" and previous.get("startedFrom"):
                started = previous["startedFrom"]
            else:
                height, width = self.art[digest].shape[:2]
                picture = self.picture_of(digest)
                started = {"mask": numpy.packbits((picture == 1).reshape(-1)).tobytes().hex(),
                           "sheer": numpy.packbits((picture == 2).reshape(-1)).tobytes().hex()}
            if status == "draft":
                record["startedFrom"] = started
            else:
                height, width = self.art[digest].shape[:2]
                before_fix = self._values(started.get("mask"), started.get("sheer"), height, width).reshape(-1)
                after_fix = self._values(mask_hex, sheer_hex, height, width).reshape(-1)
                moved = numpy.flatnonzero(before_fix != after_fix)
                if len(moved):
                    record["fix"] = {"from": started, "pixels": [[int(index), int(before_fix[index]), int(after_fix[index])] for index in moved]}
        if extra:
            record.update(extra)
        path = os.path.join(self.labels, digest + ".json")
        with self.lock:
            temporary = path + ".tmp"
            with open(temporary, "w", encoding="utf-8") as file:
                json.dump(record, file)
            os.replace(temporary, path)
            self._labels[digest] = record
            self._tile_index = None
            self.dirty.add(digest)
        flagged = []
        # A fix taken back: when this window no longer differs from the base it was copied from,
        # the "check again" marks its fix raised are lifted (user, 16 Oct 2026).
        base = self._labels.get(record.get("copiedFrom") or "")
        if status not in ("edited", "draft") or (base and mask_hex == self.fitted(base["mask"], digest, base["digest"])):
            self.lift_rechecks_from(digest)
        # A copy the user had to fix: its source may be wrong in the same place. Mark the source
        # to be looked at again; its verdict stays as it is.
        # A carried fix touched only the pixels of the fix; the rest was this window's own, so a
        # change to it says nothing about the source.
        if status == "edited" and previous and previous.get("copiedFrom") and previous["status"] in ("copy-unchecked", "copied", "edited", "draft") \
                and not previous.get("carriedFix") \
                and not (base and mask_hex == self.fitted(base["mask"], digest, base["digest"])):
            source = self._labels.get(previous["copiedFrom"])
            if source and source["status"] not in UNJUDGED:
                self.mark_recheck(source["digest"], digest)
                flagged = [source["digest"]]
        if spread and status in ("auto-ok", "edited", "no-glass", "copied"):
            spread_to = flagged + self.apply_to_twins(digest)
            if status == "edited":
                spread_to += self.carry_fix(digest)
            return spread_to
        return flagged

    def clear(self, digest):
        path = os.path.join(self.labels, digest + ".json")
        with self.lock:
            if os.path.exists(path):
                os.remove(path)
            self._labels.pop(digest, None)
            self._tile_index = None
            self.dirty.add(digest)
        self.lift_rechecks_from(digest)
        # Unchecked copies of it go back to what they were before the copy, or to nothing.
        restored = []
        for other, held in list(self._labels.items()):
            if held.get("copiedFrom") == digest and held["status"] == "copy-unchecked":
                if held.get("before"):
                    self.restore(other, held["before"])
                else:
                    self.clear(other)
                restored.append(other)
        return restored

    def mods(self):
        out = [self.mod_summary(mod) for mod in self.by_mod]
        order = {"glass found": 0, "some found": 1, "nothing found": 2}
        out.sort(key=lambda m: (m["mod"] not in ("vanilla", "sve"), order[m["verdict"]], m["name"].lower()))
        return out

    def mod_summary(self, mod):
        digests = self.by_mod[mod]
        if True:
            counts = {"total": len(digests), "todo": 0, "auto-ok": 0, "edited": 0, "no-glass": 0, "copied": 0, "noPair": 0,
                      "found": 0, "named": 0, "suggested": 0, "recheck": 0, "copiesToCheck": 0}
            for digest in digests:
                label = self.label(digest)
                if label and label.get("recheck"):
                    counts["recheck"] += 1
                if label and label.get("status") == "copy-unchecked":
                    counts["copiesToCheck"] += 1
                if label and label.get("status") in UNJUDGED:
                    counts["todo"] += 1
                elif label and label.get("status") in STATUSES:
                    counts[label["status"]] += 1
                else:
                    counts["todo"] += 1
                    if self.suggestion(digest):
                        counts["suggested"] += 1
                row = self.windows[digest]
                if row.get("method") not in ("pair", "see-through"):
                    counts["noPair"] += 1
                if row.get("glassPixels", 0) > 0:
                    counts["found"] += 1
                if row.get("strength") == "named":
                    counts["named"] += 1
            # What the mod's files and its art say together. Its files call every one of these a
            # window; the art decides whether there is glass to find.
            if counts["found"] == 0:
                verdict = "nothing found"
            elif counts["found"] * 2 >= counts["total"]:
                verdict = "glass found"
            else:
                verdict = "some found"
            return {"mod": mod, "name": self.names.get(mod, mod), "verdict": verdict, **counts}

    def where(self, digest):
        if not digest or digest not in self.windows:
            return None
        row = self.windows[digest]
        return {"id": digest, "mod": str(row["mod"]), "name": readable_name(row.get("name")), "modName": self.names.get(str(row["mod"]), str(row["mod"]))}

    def label_for_page(self, digest):
        label = self._labels.get(digest)
        return {"status": label["status"], "mask": label["mask"], "sheer": label.get("sheer") or "", "recheck": label.get("recheck"),
                "copiedFrom": label.get("copiedFrom"), "source": self.where(label.get("copiedFrom")),
                "recheckBy": self.where(label.get("recheck")), "votes": label.get("votes"),
                "carriedFix": label.get("sameFix") if label.get("carriedFix") else None} if label else None

    def publish(self):
        """Send the pages what changed: each window written, and the counts of every mod whose
        windows or whose windows' twins it touched (a new label changes the pre-fills of twins)."""
        with self.lock:
            touched, self.dirty = self.dirty, set()
        if not touched:
            return
        mods = {str(self.windows[d]["mod"]) for d in touched}
        for digest in touched:
            mods.update(str(self.windows[twin]["mod"]) for twin, _ in self.twins.get(digest, []))
        event = {"windows": [{"id": d, "mod": str(self.windows[d]["mod"]), "label": self.label_for_page(d)} for d in touched],
                 "mods": [self.mod_summary(mod) for mod in mods if mod in self.by_mod]}
        with self.changed:
            self.version += 1
            event["version"] = self.version
            self.events.append(event)
            del self.events[:-200]
            self.changed.notify_all()

    def windows_of(self, mod):
        out = []
        for digest in self.by_mod.get(mod, []):
            row = self.windows[digest]
            label = self.label(digest)
            out.append({"id": digest, "name": readable_name(row.get("name")), "kind": row.get("kind"), "method": row.get("method"),
                        "shape": row["maskShape"], "auto": row.get("mask"), "hasPair": row.get("pair") is not None and row.get("method") == "pair",
                        "seeThrough": row.get("method") == "see-through",
                        "copies": len(row.get("copies", [])),
                        "evidence": row.get("evidence"), "strength": row.get("strength"),
                        "twins": [{"id": twin, "alike": round(alike, 2), "mod": self.windows[twin]["mod"],
                                   "modName": self.names.get(str(self.windows[twin]["mod"]), str(self.windows[twin]["mod"])),
                                   "name": readable_name(self.windows[twin].get("name")),
                                   "label": (lambda l: {"status": l["status"], "mask": l["mask"], "sheer": l.get("sheer") or ""} if l else None)(self.label(twin))}
                                  for twin, alike in self.twins.get(digest, [])],
                        "suggest": None if label else self.suggestion(digest),
                        "label": self.label_for_page(digest)})
        return out

    def image(self, digest, which):
        row = self.windows[digest]
        if which == "pair":
            if not row.get("pair"):
                return None
            path, rect = row["pairPicture"], row["pair"]
        else:
            path, rect = row["picture"], row["rect"]
        x, y, w, h = rect
        with Image.open(path) as picture:
            crop = picture.convert("RGBA").crop((x, y, x + w, y + h))
        buffer = io.BytesIO()
        crop.save(buffer, "PNG")
        return buffer.getvalue()


def make_handler(corpus):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, code, body, kind):
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "max-age=86400" if kind == "image/png" else "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _json(self, value, code=200):
            self._send(code, json.dumps(value, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

        def do_GET(self):
            url = urlparse(self.path)
            query = parse_qs(url.query)
            if url.path == "/favicon.ico":
                self.send_response(204)
                self.end_headers()
            elif url.path in ("/", "/index.html"):
                self._send(200, open(PAGE, "rb").read(), "text/html; charset=utf-8")
            elif url.path == "/api/mods":
                self._json(corpus.mods())
            elif url.path == "/api/events":
                self.stream_events()
            elif url.path == "/api/windows":
                self._json(corpus.windows_of(query.get("mod", [""])[0]))
            elif url.path == "/api/image":
                digest, which = query.get("id", [""])[0], query.get("which", ["day"])[0]
                if digest not in corpus.windows:
                    return self._json({"error": "unknown window"}, 404)
                body = corpus.image(digest, which)
                if body is None:
                    return self._json({"error": "no pair"}, 404)
                self._send(200, body, "image/png")
            else:
                self._json({"error": "not found"}, 404)

        def stream_events(self):
            """Server-sent events: every change, pushed to the page as it happens."""
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "keep-alive")
            self.end_headers()
            seen = corpus.version
            try:
                # retry: a dropped line is tried again after a second, not the browser's default three
                self.wfile.write(f"retry: 1000\nevent: hello\ndata: {seen}\n\n".encode()); self.wfile.flush()
                while True:
                    with corpus.changed:
                        corpus.changed.wait_for(lambda: corpus.version > seen, timeout=15)
                        fresh = [event for event in corpus.events if event["version"] > seen]
                    if not fresh:
                        self.wfile.write(b": still here\n\n"); self.wfile.flush()
                        continue
                    for event in fresh:
                        self.wfile.write(f"data: {json.dumps(event, ensure_ascii=False)}\n\n".encode("utf-8"))
                        seen = event["version"]
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
                return

        def do_POST(self):
            try:
                self.handle_post()
            finally:
                corpus.publish()

        def handle_post(self):
            url = urlparse(self.path)
            length = int(self.headers.get("Content-Length", "0"))
            body = json.loads(self.rfile.read(length) or b"{}")
            if url.path == "/api/spread-all":
                # Once over every judged window: their recolours and repeats get the same answer.
                changed = set()
                for source in list(corpus._labels):
                    if corpus._labels[source]["status"] in ("auto-ok", "edited", "no-glass", "copied"):
                        changed.update(corpus.apply_to_twins(source))
                return self._json({"ok": True, "changed": len(changed)})
            if url.path == "/api/mod-no-glass":
                # Every window of the mod nobody has judged yet: no glass. Judged ones are kept.
                marked = 0
                for digest in corpus.by_mod.get(str(body.get("mod")), []):
                    if not corpus.label(digest):
                        row = corpus.windows[digest]
                        corpus.save(digest, "no-glass", "00" * ((row["maskShape"][0] * row["maskShape"][1] + 7) // 8), spread=False)
                        marked += 1
                return self._json({"ok": True, "marked": marked})
            digest = body.get("id")
            if digest not in corpus.windows:
                return self._json({"error": "unknown window"}, 404)
            if url.path == "/api/label":
                status = body.get("status")
                if status not in STATUSES:
                    return self._json({"error": "bad status"}, 400)
                spread = corpus.save(digest, status, body.get("mask", ""), sheer_hex=body.get("sheer"))
                self._json({"ok": True, "spread": [{"id": twin, "mod": str(corpus.windows[twin]["mod"]), "status": corpus.label(twin)["status"],
                                                    "mask": corpus.label(twin)["mask"], "sheer": corpus.label(twin).get("sheer") or "", "recheck": corpus.label(twin).get("recheck"),
                                                    "copiedFrom": corpus.label(twin).get("copiedFrom"), "name": readable_name(corpus.windows[twin].get("name")),
                                                    "modName": corpus.names.get(str(corpus.windows[twin]["mod"]), str(corpus.windows[twin]["mod"]))}
                                                   for twin in dict.fromkeys(spread)]})
            elif url.path == "/api/still-right":
                # Looked at again and it stands: the "check again" mark goes, the verdict stays.
                label = corpus.label(digest)
                if label and label.get("recheck"):
                    record = {key: value for key, value in label.items() if key != "recheck"}
                    path = os.path.join(corpus.labels, digest + ".json")
                    with corpus.lock:
                        with open(path + ".tmp", "w", encoding="utf-8") as file:
                            json.dump(record, file)
                        os.replace(path + ".tmp", path)
                        corpus._labels[digest] = record
                        corpus.dirty.add(digest)
                self._json({"ok": True})
            elif url.path == "/api/copy-to-twins":
                # This window's glass, given to every same-shape window nobody has judged yet.
                label = corpus.label(digest)
                if not label or label["status"] == "no-glass":
                    return self._json({"error": "label this window first"}, 400)
                given = 0
                for twin, _ in corpus.twins.get(digest, []):
                    if not corpus.label(twin):
                        corpus.save(twin, "copied", label["mask"], copied_from=digest, spread=False)
                        given += 1
                self._json({"ok": True, "given": given})
            elif url.path == "/api/clear":
                restored = corpus.clear(digest)
                self._json({"ok": True, "spread": [{"id": other, "mod": corpus.windows[other]["mod"],
                                                    "status": (corpus.label(other) or {}).get("status"),
                                                    "mask": (corpus.label(other) or {}).get("mask"),
                                                    "sheer": (corpus.label(other) or {}).get("sheer") or "",
                                                    "recheck": (corpus.label(other) or {}).get("recheck"),
                                                    "copiedFrom": (corpus.label(other) or {}).get("copiedFrom")} for other in restored]})
            else:
                self._json({"error": "not found"}, 404)

    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    corpus = Corpus(args.corpus)
    print(f"{len(corpus.windows)} windows from {len(corpus.by_mod)} mods; labels in {corpus.labels}")
    print(f"open http://127.0.0.1:{args.port}", flush=True)
    class LabelerServer(ThreadingHTTPServer):
        def handle_error(self, request, client_address):
            # A page closed or reloaded mid-request is not an error worth a traceback.
            if isinstance(sys.exc_info()[1], (ConnectionAbortedError, ConnectionResetError, BrokenPipeError)):
                return
            super().handle_error(request, client_address)

    server = LabelerServer(("127.0.0.1", args.port), make_handler(corpus))
    server.daemon_threads = True    # open live streams never keep the server from stopping
    server.serve_forever()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
