"""Turn a sheet painted against a mod's art into a VARIANT the mod can use for that art.

The shipped labels are painted on one picture. A pack that repaints a tilesheet under the same
name gives a different picture, and the shipped label for that tile is then wrong: that is the
window reflections landing on the base game's buildings. A variant is the same tile painted
against the OTHER picture, tied to the fingerprints of the art it was painted for, so it only
ever applies where that art is really loaded.

    python makevariant.py --name elle-town --describe "Elle's Town Buildings" \\
        --labels <export.json> --passes elle-town-Elle elle-town-Earthy elle-town-Starblue

  --labels   an HF Studio export, painted while that mod's art was the art on screen
  --passes   the radiance_artfingerprint passes taken on that same art, by name

Several passes on purpose. Four palettes of one pack repaint a window without moving it, so one
painted label is right for all four; listing all four fingerprints against one label is how that
gets said, and it is four times less painting than doing it per palette.

Only tiles whose label actually DIFFERS from the shipped one are written. A tile the mod left
alone already has a correct label and a variant for it would be a second copy to keep in step.

Writes labels/art-variants.json, merging with whatever is already there by variant name.
"""
import argparse, base64, json, os, sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PASSES = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio", "fingerprints")
SHIPPED_LABELS = os.path.join(REPO, "labels", "water-labels.json")
VARIANTS = os.path.join(REPO, "labels", "art-variants.json")


def read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def painted_tiles(document):
    """{sheet: {index: base64}} out of an HF Studio export or the shipped DB."""
    out = {}
    for sheet, body in (document.get("sheets") or {}).items():
        tiles = body.get("tiles") if isinstance(body, dict) else None
        if tiles:
            out[sheet] = dict(tiles)
    return out


def fingerprints_from(pass_names):
    """{sheet: {index: [fingerprint, ...]}} unioned across the named passes."""
    out = {}
    for name in pass_names:
        path = os.path.join(PASSES, name + ".json")
        if not os.path.exists(path):
            raise SystemExit(f"no such fingerprint pass: {path}")
        document = read_json(path)
        for sheet, body in (document.get("sheets") or {}).items():
            for index, fingerprint in (body.get("tiles") or {}).items():
                seen = out.setdefault(sheet, {}).setdefault(index, [])
                if fingerprint not in seen:
                    seen.append(fingerprint)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True, help="short id for this variant, e.g. elle-town")
    parser.add_argument("--describe", default="", help="what a reader should call it")
    parser.add_argument("--labels", required=True, help="the export painted against that art")
    parser.add_argument("--passes", nargs="+", required=True, help="fingerprint pass names for that art")
    parser.add_argument("--sheets", help="only these sheets, comma separated")
    args = parser.parse_args()

    base = painted_tiles(read_json(SHIPPED_LABELS))
    painted = painted_tiles(read_json(args.labels))
    art = fingerprints_from(args.passes)
    only = {s.strip() for s in args.sheets.split(",")} if args.sheets else None

    sheets_out, written, skipped_same, skipped_unfingerprinted = {}, 0, 0, 0
    for sheet, tiles in painted.items():
        if only and sheet not in only:
            continue
        for index, label in tiles.items():
            if base.get(sheet, {}).get(index) == label:
                skipped_same += 1
                continue                     # the mod left this tile alone, base already covers it
            prints = art.get(sheet, {}).get(index)
            if not prints:
                skipped_unfingerprinted += 1
                continue                     # no reading of this tile's art, so nothing to tie it to
            try:
                if len(base64.b64decode(label)) != 256:
                    continue
            except Exception:
                continue
            sheets_out.setdefault(sheet, {}).setdefault(index, []).append(
                {"source": args.name, "art": prints, "label": label})
            written += 1

    existing = read_json(VARIANTS) if os.path.exists(VARIANTS) else {"format": 1, "sources": {}, "sheets": {}}
    # Replace this variant's own entries wholesale; leave everybody else's alone. Merging one
    # name's entries tile by tile would quietly keep a tile somebody deliberately unpainted.
    for sheet, tiles in list((existing.get("sheets") or {}).items()):
        for index, entries in list(tiles.items()):
            kept = [e for e in entries if e.get("source") != args.name]
            if kept:
                tiles[index] = kept
            else:
                del tiles[index]
        if not tiles:
            del existing["sheets"][sheet]
    for sheet, tiles in sheets_out.items():
        for index, entries in tiles.items():
            existing.setdefault("sheets", {}).setdefault(sheet, {}).setdefault(index, []).extend(entries)
    existing.setdefault("sources", {})[args.name] = args.describe or args.name
    existing["format"] = 1

    with open(VARIANTS, "w", encoding="utf-8") as f:
        json.dump(existing, f)

    print(f"variant \"{args.name}\": {written} tile(s) written across {len(sheets_out)} sheet(s)")
    print(f"  fingerprints from  : {', '.join(args.passes)}")
    print(f"  same as shipped    : {skipped_same} (left to the base label)")
    print(f"  no art reading     : {skipped_unfingerprinted} (take a fingerprint pass on that art)")
    print(f"  written to {VARIANTS} ({os.path.getsize(VARIANTS) / 1024:.0f} KB)")
    if skipped_unfingerprinted and not written:
        print("  nothing was written: the passes named do not cover the sheets that were painted")


if __name__ == "__main__":
    main()
