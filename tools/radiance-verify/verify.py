"""Compare two `radiance_dump` captures away from the game.

The point of this tool is one question a refactor cannot answer from reading the code: did the
pixels move? Capture a scene, change the code, capture the same scene, and run

    python tools/radiance-verify/verify.py -a <baseline> -b <candidate>

Exit code is 0 when nothing moved, 1 when something did, and 2 when the two captures were never
comparable in the first place (different scene, different config, clock not frozen). The third
case matters more than it looks: a diff between two captures of different moments is noise that
reads exactly like a regression, and a tool that reports it as one is worse than no tool.

Buffer names come from the capture's own metadata, so nothing here needs updating when the
pipeline grows a new render target.

Usage:
    verify.py -a DIR -b DIR          compare two captures
    verify.py -d DIR                 inventory + sanity-check one capture
    verify.py -a DIR -b DIR --png    also write per-buffer diff images next to the candidate
"""

import argparse
import gzip
import json
import os
import sys

import numpy as np

# Scene facts that must match for a comparison to mean anything. A capture taken at a different
# time of day, in different weather or at a different zoom is a different picture of a different
# thing, and no amount of pixel maths makes the diff informative.
SCENE_KEYS = ["location", "timeOfDay", "season", "dayOfMonth", "weather", "playerTile", "viewport", "zoom"]

# Eased amounts freeze mode is supposed to have settled. If a capture's presences differ from its
# counterpart's, the clock was frozen but the scene had not converged when it was taken.
PRESENCE_TOL = 1e-4

# Buffers that are COPIES of the game's own frame rather than something the mod computed. They
# inherit vanilla drift exactly as frame_out does - a critter that moved moves in the copy too.
#
# frame_out gets that drift subtracted pixel by pixel, but these cannot: they are held at other
# resolutions and offsets (mirror_source is the chain buffer plus a fixed side/top reach, the
# chain buffer is the window scaled by 1/zoom), so the drift mask does not line up and a
# hand-rolled rescale would hide real regressions in the slop.
#
# So the rule here is about what can be CONCLUDED rather than about geometry: if the game's own
# frame is identical, a copy of it must be identical too, and a difference is real and counts. If
# the game's frame drifted, a difference in the copy is inherited, cannot be separated at this
# resolution, and is reported without deciding the verdict on its own. Without this, every outdoor
# revisit reports mirror_source changed forever, and a verifier that always complains is one
# people stop reading.
SCENE_COPY_BUFFERS = {"mirror_source"}


def load(path):
    meta_path = os.path.join(path, "metadata.json")
    if not os.path.isfile(meta_path):
        raise SystemExit(f"not a capture (no metadata.json): {path}")
    with open(meta_path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def read_buffer(path, entry):
    """Return the buffer as (height, width, channels) uint8."""
    raw = os.path.join(path, entry["file"])
    with gzip.open(raw, "rb") as fh:
        data = np.frombuffer(fh.read(), dtype=np.uint8)
    h, w, bpp = entry["height"], entry["width"], entry["bytesPerPixel"]
    if data.size != h * w * bpp:
        raise ValueError(f"{entry['file']}: expected {h * w * bpp} bytes, got {data.size}")
    return data.reshape(h, w, bpp)


def comparable(a, b, allow_settings=()):
    """Reasons these two captures cannot be compared. Empty list = go ahead.

    Returns (problems, notes): notes are differences worth printing that do not stop
    the comparison. `allow_settings` names settings whose values are EXPECTED to differ
    (--allow-setting), for the one case where that is the point of the run: measuring
    what a quality setting actually costs the picture.
    """
    problems = []
    notes = []
    for name, meta in (("A", a), ("B", b)):
        if not meta.get("frozen"):
            problems.append(f"capture {name} was taken with the clock running (run radiance_freeze first)")
    sa, sb = a.get("scene", {}), b.get("scene", {})
    for key in SCENE_KEYS:
        if sa.get(key) != sb.get(key):
            problems.append(f"scene.{key} differs: A={sa.get(key)!r} B={sb.get(key)!r}")
    # Settings: a shared key holding different values means the two captures were taken
    # with different settings and nothing can be concluded. A key that exists on ONE side
    # only is a setting the release ADDED or RETIRED, which is exactly the situation you
    # want to verify (does the new setting, at its default, change the old picture?) - so
    # it is reported and the comparison continues. Naming the keys matters: a bare
    # "config differs" used to make every cross-release comparison unusable.
    ca, cb = a.get("config", {}) or {}, b.get("config", {}) or {}
    changed = sorted(k for k in set(ca) & set(cb) if ca[k] != cb[k])
    allowed = [k for k in changed if k in allow_settings]
    changed = [k for k in changed if k not in allow_settings]
    if allowed:
        notes.append("settings deliberately compared across values: "
                     + ", ".join(f"{k} A={ca[k]!r} B={cb[k]!r}" for k in allowed))
    if changed:
        problems.append("settings differ between the captures (not a regression): "
                        + ", ".join(f"{k} A={ca[k]!r} B={cb[k]!r}" for k in changed))
    added = sorted(set(cb) - set(ca))
    removed = sorted(set(ca) - set(cb))
    if added:
        notes.append("settings present only in B (added since A): " + ", ".join(f"{k}={cb[k]!r}" for k in added))
    if removed:
        notes.append("settings present only in A (retired since): " + ", ".join(f"{k}={ca[k]!r}" for k in removed))
    ra, rb = a.get("render", {}), b.get("render", {})
    if ra != rb:
        problems.append(f"render size differs: A={ra} B={rb}")
    return problems, notes


def presence_drift(a, b):
    """Eased amounts that had not settled to the same value in both captures."""
    pa, pb = a.get("presence", {}), b.get("presence", {})
    out = []
    for key in sorted(set(pa) | set(pb)):
        va, vb = pa.get(key), pb.get(key)
        if isinstance(va, (int, float)) and isinstance(vb, (int, float)):
            if abs(va - vb) > PRESENCE_TOL:
                out.append((key, va, vb))
        elif va != vb:
            out.append((key, va, vb))
    return out


def diff_buffer(ba, bb):
    """Summarise how two buffers differ. None when they are byte-identical."""
    if ba.shape != bb.shape:
        return {"shape": (ba.shape, bb.shape)}
    if np.array_equal(ba, bb):
        return None
    # int16 so the subtraction does not wrap around in uint8.
    delta = np.abs(ba.astype(np.int16) - bb.astype(np.int16))
    per_pixel = delta.max(axis=2)
    moved = per_pixel > 0
    ys, xs = np.nonzero(moved)
    return {
        "pixels": int(moved.sum()),
        "total": int(moved.size),
        "fraction": float(moved.sum()) / moved.size,
        "max_delta": int(delta.max()),
        "mean_delta_over_moved": float(delta[delta > 0].mean()),
        "bbox": [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())],
        "per_channel_max": [int(delta[:, :, c].max()) for c in range(delta.shape[2])],
    }


def box_dilate(mask, radius):
    """True wherever any True lies within a (2r+1) box — integral-image, O(pixels)."""
    summed = np.cumsum(np.cumsum(mask.astype(np.int32), axis=0), axis=1)
    summed = np.pad(summed, ((1, 0), (1, 0)))
    h, w = mask.shape
    y0 = np.clip(np.arange(h) - radius, 0, h)
    y1 = np.clip(np.arange(h) + radius + 1, 0, h)
    x0 = np.clip(np.arange(w) - radius, 0, w)
    x1 = np.clip(np.arange(w) + radius + 1, 0, w)
    return (summed[y1][:, x1] - summed[y0][:, x1]
            - summed[y1][:, x0] + summed[y0][:, x0]) > 0


# How far (px) a change in the game's own frame can plausibly spread through the effect
# stack (bloom blur, fog sampling, water displacement all reach, none of them this far).
VANILLA_DRIFT_HALO_PX = 96


def write_png(path, ba, bb):
    """Diff image: red where the candidate is brighter, blue where it is darker, amplified so a
    one-level difference is still visible. Optional — a missing Pillow is not an error."""
    try:
        from PIL import Image
    except ImportError:
        return None
    a = ba.astype(np.int16).mean(axis=2)
    b = bb.astype(np.int16).mean(axis=2)
    d = b - a
    img = np.zeros((a.shape[0], a.shape[1], 3), dtype=np.uint8)
    img[:, :, 0] = np.clip(d * 8, 0, 255)
    img[:, :, 2] = np.clip(-d * 8, 0, 255)
    Image.fromarray(img).save(path)
    return path


def describe(meta):
    s = meta.get("scene", {})
    return (f"{s.get('location')} {s.get('season')} day {s.get('dayOfMonth')} "
            f"{s.get('timeOfDay')} {s.get('weather')} @ tile {s.get('playerTile')}")


def cmd_inspect(path):
    meta = load(path)
    print(f"capture : {meta.get('name')}  (mod {meta.get('modVersion')}, {meta.get('capturedUtc')})")
    print(f"scene   : {describe(meta)}")
    print(f"frozen  : {meta.get('frozen')} (tick {meta.get('pinnedTicks')})")
    mask = meta.get("mask", {})
    print(f"mask    : origin {mask.get('originTile')} anyWater={mask.get('anyWater')} "
          f"labels v{mask.get('labelVersion')} jobInFlight={mask.get('jobInFlight')}")
    if mask.get("jobInFlight"):
        print("  ! a water-mask rebuild was still running when this was captured; the mask buffers "
              "may be one frame behind the frame buffers")
    print("buffers :")
    problems = 0
    for name, entry in sorted(meta.get("buffers", {}).items()):
        buf = read_buffer(path, entry)
        flat = "  ALL ZERO" if not buf.any() else ""
        if flat:
            problems += 1
        print(f"  {name:16s} {entry['width']}x{entry['height']} {entry['format']:8s} "
              f"{entry['bytes'] / 1e6:6.2f} MB{flat}")
    if not meta.get("frozen"):
        print("\n! clock was running: this capture cannot be compared with another run")
        return 2
    return 1 if problems else 0


def cmd_compare(dir_a, dir_b, png, allow_settings=()):
    a, b = load(dir_a), load(dir_b)
    print(f"A: {dir_a}\n   {describe(a)}  (mod {a.get('modVersion')})")
    print(f"B: {dir_b}\n   {describe(b)}  (mod {b.get('modVersion')})\n")

    problems, notes = comparable(a, b, allow_settings)
    if problems:
        print("NOT COMPARABLE:")
        for p in problems:
            print(f"  - {p}")
        return 2
    if notes:
        for n in notes:
            print(f"note: {n}")
        print()

    drift = presence_drift(a, b)
    if drift:
        print("presence drift (the scene had not settled when one of these was captured):")
        for key, va, vb in drift:
            print(f"  - {key}: A={va} B={vb}")
        print()

    buffers_a, buffers_b = a.get("buffers", {}), b.get("buffers", {})
    only_a = sorted(set(buffers_a) - set(buffers_b))
    only_b = sorted(set(buffers_b) - set(buffers_a))
    for name in only_a:
        print(f"  {name}: present in A, missing from B")
    for name in only_b:
        print(f"  {name}: present in B, missing from A")

    # The game's own frame (frame_in) is NOT deterministic across runs: critters, chimney
    # smoke and animated map tiles run on game logic the freeze deliberately leaves alone.
    # Where frame_in moved, frame_out moving is the game's doing, not the mod's — so those
    # pixels (plus a halo for effect spread) are excluded from the frame_out verdict.
    #
    # ⚠️ frame_in IS NOT THE GAME UNTOUCHED. The mod's SHADOWS are already in it: they are drawn
    # at the World_Sorted render step and _sceneRenderTarget is captured afterwards, in
    # RenderedWorld. So for a change that only moves shadows, the drift mask below is built FROM
    # that change and then forgives it. Measured 2026-08-17, in the strongest possible form:
    # a pair captured with DirectionalShadowsEnabled True vs False — every shadow in the room
    # switched off — differed by 2,935 px in frame_in and 5,341 in frame_out, no internal buffer
    # moved, and the verdict below read "IDENTICAL apart from vanilla drift. Not a mod regression."
    #
    # Hence the shadow-path line printed after the buffer table: for shadow work, only a
    # BYTE-IDENTICAL frame_in is evidence. An exit code cannot carry this, because frame_in drifts
    # at nearly every outdoor spot and failing on that would make the tool useless.
    vanilla_moved = None
    if "frame_in" in buffers_a and "frame_in" in buffers_b:
        fa = read_buffer(dir_a, buffers_a["frame_in"])
        fb = read_buffer(dir_b, buffers_b["frame_in"])
        if fa.shape == fb.shape and not np.array_equal(fa, fb):
            vanilla_moved = np.abs(fa.astype(np.int16) - fb.astype(np.int16)).max(axis=2) > 0

    changed = []
    frame_out_outside_halo = 0
    for name in sorted(set(buffers_a) & set(buffers_b)):
        ba = read_buffer(dir_a, buffers_a[name])
        bb = read_buffer(dir_b, buffers_b[name])
        d = diff_buffer(ba, bb)
        if d is None:
            print(f"  {name:16s} identical")
            continue
        changed.append(name)
        if "shape" in d:
            print(f"  {name:16s} SIZE CHANGED {d['shape'][0]} -> {d['shape'][1]}")
            continue
        print(f"  {name:16s} DIFFERS  {d['pixels']:,} px ({d['fraction'] * 100:.4f}%)  "
              f"max delta {d['max_delta']}  mean {d['mean_delta_over_moved']:.2f}  "
              f"bbox {d['bbox']}  per-channel max {d['per_channel_max']}")
        if name == "frame_out" and vanilla_moved is not None and vanilla_moved.shape == ba.shape[:2]:
            moved = np.abs(ba.astype(np.int16) - bb.astype(np.int16)).max(axis=2) > 0
            outside = moved & ~box_dilate(vanilla_moved, VANILLA_DRIFT_HALO_PX)
            frame_out_outside_halo = int(outside.sum())
            print(f"  {'':16s} vanilla drift: frame_in moved {int(vanilla_moved.sum()):,} px; "
                  f"frame_out pixels OUTSIDE the {VANILLA_DRIFT_HALO_PX}px drift halo: "
                  f"{frame_out_outside_halo:,}")
        if name in SCENE_COPY_BUFFERS and vanilla_moved is not None:
            print(f"  {'':16s} inherited drift: this buffer is a copy of the game's frame, which "
                  f"moved. Not assessable at this resolution; not counted.")
        if png:
            out = write_png(os.path.join(dir_b, f"diff_{name}.png"), ba, bb)
            if out:
                print(f"  {'':16s} -> {out}")

    # Said out loud, every run, because the buffer table cannot say it: the mod's shadows live
    # inside frame_in (see the note above), so a shadow change is inside the thing that excuses it.
    if "frame_in" in buffers_a and "frame_in" in buffers_b:
        if vanilla_moved is None:
            print("shadow path: frame_in is BYTE-IDENTICAL, so shadows are certified here.")
        else:
            print(f"shadow path: frame_in differs ({int(vanilla_moved.sum()):,} px), and the mod's "
                  "shadows are inside frame_in, so a shadow-only change CANNOT be certified at this "
                  "spot, whatever the verdict below says.")
    print()
    if changed or only_a or only_b:
        # A scene copy only counts when the game's own frame held still; see SCENE_COPY_BUFFERS.
        internal = [c for c in changed
                    if c not in ("frame_in", "frame_out")
                    and not (c in SCENE_COPY_BUFFERS and vanilla_moved is not None)]
        frame_out_is_real = "frame_out" in changed and (
            vanilla_moved is None or frame_out_outside_halo > 0)
        # frame_out is the one the player would notice; the masks say which stage moved it.
        if frame_out_is_real:
            print("REGRESSION: the composed frame changed beyond the game's own drift.")
            return 1
        if internal:
            print("CHANGED: internal buffers moved but the composed frame did not. "
                  "Safe to ship, worth understanding.")
            return 1
        if only_a or only_b:
            print("CHANGED: the two captures do not hold the same buffers.")
            return 1
        print("IDENTICAL apart from vanilla drift: the game's own frame differed (critters/"
              "animated tiles) and every composed-frame difference sits inside that drift halo. "
              "Not a mod regression.")
        return 0

    print("IDENTICAL: every buffer matches byte for byte.")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-a", "--baseline", help="baseline capture directory")
    ap.add_argument("-b", "--candidate", help="candidate capture directory")
    ap.add_argument("-d", "--inspect", help="inventory one capture directory")
    ap.add_argument("--png", action="store_true", help="write per-buffer diff images (needs Pillow)")
    ap.add_argument("--allow-setting", action="append", metavar="KEY",
                    help="compare even though this setting differs (e.g. RenderScale) - use when "
                         "measuring what a quality setting costs, never to silence a surprise")
    args = ap.parse_args()

    if args.inspect:
        return cmd_inspect(args.inspect)
    if args.baseline and args.candidate:
        return cmd_compare(args.baseline, args.candidate, args.png, set(args.allow_setting or ()))
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
