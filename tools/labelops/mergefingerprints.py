"""Fold radiance_artfingerprint passes into the art fingerprints the mod ships.

Each pass is one reading of "what art is behind every labelled tile" under one set of mods, taken
in game by radiance_artfingerprint and left in Documents\\HF-Studio\\fingerprints. This unions them
into labels/art-fingerprints.json, which is the list a label is allowed to appear on: art whose
fingerprint is not in it gets no label, so a pass that was never taken is a set of players whose
ripple and glass go quiet.

    python mergefingerprints.py                       merge every pass into the shipped file
    python mergefingerprints.py --only vanilla elle-*  merge just these
    python mergefingerprints.py --compare a b          say which labelled tiles differ between two

The compare mode is the one worth running first. Two passes that disagree on a tile are two
pictures under one name, and the count is the number of labels that pass would cost the other
pass's players.
"""
import argparse, fnmatch, json, os, sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PASSES = os.path.join(os.path.expanduser("~"), "Documents", "HF-Studio", "fingerprints")
SHIPPED = os.path.join(REPO, "labels", "art-fingerprints.json")


def read_pass(path):
    """One pass as ({sheet: {tileIndex: fingerprint}}, [packs that repaint map art])."""
    with open(path, encoding="utf-8") as f:
        document = json.load(f)
    out = {}
    for sheet, body in (document.get("sheets") or {}).items():
        tiles = body.get("tiles") if isinstance(body, dict) else None
        if tiles:
            out[sheet] = dict(tiles)
    return out, list(document.get("repaintedBy") or [])


def load_passes(patterns):
    if not os.path.isdir(PASSES):
        raise SystemExit(f"no passes yet: {PASSES} does not exist")
    found = {}
    for name in sorted(os.listdir(PASSES)):
        if not name.endswith(".json"):
            continue
        label = name[:-5]
        if patterns and not any(fnmatch.fnmatch(label, p) for p in patterns):
            continue
        found[label] = read_pass(os.path.join(PASSES, name))
    if not found:
        raise SystemExit("no passes matched")
    return found


def compare(left_label, right_label, passes):
    left = passes.get(left_label)[0] if left_label in passes else None
    right = passes.get(right_label)[0] if right_label in passes else None
    if left is None or right is None:
        raise SystemExit(f"need both passes; have {', '.join(sorted(passes))}")
    shared_sheets = sorted(set(left) & set(right))
    only_left = sorted(set(left) - set(right))
    only_right = sorted(set(right) - set(left))
    same = differ = 0
    rows = []
    for sheet in shared_sheets:
        a, b = left[sheet], right[sheet]
        shared_tiles = set(a) & set(b)
        sheet_differ = sum(1 for t in shared_tiles if a[t] != b[t])
        same += len(shared_tiles) - sheet_differ
        differ += sheet_differ
        if sheet_differ:
            rows.append((sheet_differ, len(shared_tiles), sheet))
    print(f"{left_label}  vs  {right_label}")
    print(f"  labelled tiles both passes saw : {same + differ}")
    print(f"  same art                       : {same}")
    print(f"  DIFFERENT art                  : {differ}")
    if only_left:
        print(f"  sheets only {left_label} saw   : {len(only_left)}")
    if only_right:
        print(f"  sheets only {right_label} saw  : {len(only_right)}")
    if rows:
        print("\n  sheets that differ, worst first:")
        for sheet_differ, total, sheet in sorted(rows, reverse=True):
            print(f"    {sheet:<40} {sheet_differ:>5} of {total}")


def glass_tiles():
    """(sheet, index) for every shipped label that paints mirror, window or glass.

    The guard in the mod only ever looks at those three classes, so shipping a fingerprint for
    anything else would be dead weight that quietly invites the guard to grow. Measured on this
    machine, the difference is 2,847 tiles rather than 20,202.
    """
    import base64
    with open(os.path.join(REPO, "labels", "water-labels.json"), encoding="utf-8") as f:
        labels = json.load(f)
    classes = labels["classes"]
    wanted = {classes.index(name) for name in ("mirror", "window", "glass")}
    out = set()
    for sheet, body in labels["sheets"].items():
        for index, painted in (body.get("tiles") or {}).items():
            if set(base64.b64decode(painted)) & wanted:
                out.add((sheet, index))
    return out


def merge(passes):
    keep = glass_tiles()
    merged = {}
    provenance = {}
    for label, (body, repainters) in passes.items():
        provenance[label] = repainters
        for sheet, tiles in body.items():
            for index, fingerprint in tiles.items():
                if (sheet, index) not in keep:
                    continue
                seen = merged.setdefault(sheet, {}).setdefault(index, [])
                if fingerprint not in seen:
                    seen.append(fingerprint)
    tiles = sum(len(t) for t in merged.values())
    variants = sum(1 for t in merged.values() for v in t.values() if len(v) > 1)
    os.makedirs(os.path.dirname(SHIPPED), exist_ok=True)
    with open(SHIPPED, "w", encoding="utf-8") as f:
        json.dump({"format": 1, "passes": sorted(passes),
                   "repaintedBy": provenance, "sheets": merged}, f)
    print(f"merged {len(passes)} pass(es):")
    for label in sorted(passes):
        packs = provenance.get(label) or []
        print(f"    {label}: art from " + (", ".join(packs) if packs else "no map-art pack, so the base game"))
    print(f"  sheets                      : {len(merged)}")
    print(f"  tiles guarded               : {tiles}")
    print(f"  tiles with more than one art: {variants}")
    print(f"  written to {SHIPPED}")
    print(f"  {os.path.getsize(SHIPPED) / 1024:.0f} KB")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", nargs="*", default=None, help="pass labels or globs")
    parser.add_argument("--compare", nargs=2, metavar=("A", "B"))
    args = parser.parse_args()
    passes = load_passes(args.only)
    if args.compare:
        compare(args.compare[0], args.compare[1], load_passes(None))
    else:
        merge(passes)


if __name__ == "__main__":
    main()
