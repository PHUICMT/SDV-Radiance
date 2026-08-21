"""Is the dump what it says it is? Structural check on a v3 maps.json.

A sweep is thirty profiles and several hours, and the failures that matter are quiet: a
location file that was never written, a sheetArt naming a PNG that is not there, two profiles
storing the same map twice because the stamp does not actually distinguish them. Every one of
those looks like a finished dump until something opens it.

    python tools/labelops/checkdump.py
    python tools/labelops/checkdump.py --deep      also open every location file
    python tools/labelops/checkdump.py --path X    a dump somewhere other than HF-Studio

Exit code is 1 when a check fails, so it can gate a sweep.
"""
import argparse, base64, collections, json, os, sys

sys.stdout.reconfigure(encoding="utf-8")

HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def cells_of(entry):
    """Every layer's cell data as one string, for telling two versions of a place apart."""
    return "|".join(str((layer or {}).get("cells", "")) for layer in (entry.get("layers") or []))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--path", default=os.path.join(HFDIR, "maps.json"))
    parser.add_argument("--deep", action="store_true", help="open every location file")
    arguments = parser.parse_args()

    if not os.path.exists(arguments.path):
        sys.exit(f"no dump at {arguments.path}")
    root = os.path.dirname(arguments.path)
    document = load(arguments.path)
    problems = []

    fmt = document.get("format")
    print(f"format             : {fmt}")
    if fmt != "hf-mapdump-v3":
        problems.append(f"format is {fmt}, not hf-mapdump-v3")

    profiles = document.get("profiles") or []
    locations = document.get("locations") or {}
    print(f"profiles           : {len(profiles)}  {', '.join(profiles[:6])}"
          + (" ..." if len(profiles) > 6 else ""))
    print(f"map versions       : {len(locations):,}")
    if not profiles:
        problems.append("no profiles listed: nothing can be attributed")

    # ---- one name, several versions ----------------------------------------------------------
    by_name = collections.defaultdict(list)
    for key, record in locations.items():
        by_name[record.get("name") or key].append(key)
    many = {n: k for n, k in by_name.items() if len(k) > 1}
    print(f"distinct places    : {len(by_name):,}  of which {len(many):,} have more than one version")

    stamps = collections.defaultdict(set)
    for name, keys in by_name.items():
        for key in keys:
            stamp = locations[key].get("variant")
            if stamp in stamps[name]:
                problems.append(f"{name}: two entries share the variant stamp {stamp}")
            stamps[name].add(stamp)

    # A version that carries no name at all is a v2 record that slipped through the merge.
    nameless = [k for k, r in locations.items() if not r.get("name")]
    if nameless:
        problems.append(f"{len(nameless)} entr(ies) carry no name: {', '.join(nameless[:4])}")

    # ---- the art ------------------------------------------------------------------------------
    sheets_dir = os.path.join(root, "sheets")
    on_disk = set(os.listdir(sheets_dir)) if os.path.isdir(sheets_dir) else set()
    claimed, missing_art = set(), collections.Counter()
    for field in ("artPng", "artPngBySrc"):
        for value in (document.get(field) or {}).values():
            base = os.path.basename(str(value))
            claimed.add(base)
            if base not in on_disk:
                missing_art[field] += 1
    print(f"sheet PNGs on disk : {len(on_disk):,}   claimed by the index: {len(claimed):,}")
    for field, count in missing_art.items():
        problems.append(f"{field} names {count} file(s) that are not in sheets/")

    # ---- the location files -------------------------------------------------------------------
    files = [str((r or {}).get("file") or "") for r in locations.values()]
    absent = [f for f in files if not f or not os.path.exists(os.path.join(root, f.replace("/", os.sep)))]
    print(f"location files     : {len(files) - len(absent):,} present, {len(absent):,} absent")
    if absent:
        problems.append(f"{len(absent)} location file(s) missing, e.g. {absent[0]}")
    if len(set(files)) != len(files):
        problems.append("two locations claim the same file, so one has overwritten the other")

    # ---- the check that costs something, and is the only one that proves the point ------------
    if arguments.deep:
        print("\nopening every location file...")
        seen_art, duplicates, unreadable = {}, 0, 0
        for name, keys in by_name.items():
            fingerprints = {}
            for key in keys:
                relative = str(locations[key].get("file") or "")
                try:
                    entry = load(os.path.join(root, relative.replace("/", os.sep)))
                except Exception:
                    unreadable += 1
                    continue
                art = "|".join(str(a) for a in (entry.get("sheetArt") or []))
                mark = cells_of(entry) + "##" + art
                if mark in fingerprints:
                    duplicates += 1
                    problems.append(f"{name}: {key} and {fingerprints[mark]} are the same map "
                                    f"stored twice")
                fingerprints[mark] = key
                for one in (entry.get("sheetArt") or []):
                    if one:
                        seen_art[os.path.basename(str(one))] = name
        for base, where in list(seen_art.items()):
            if base not in on_disk:
                problems.append(f"{where} names sheet art that is not on disk: {base}")
                break
        print(f"  {unreadable} unreadable, {duplicates} stored-twice, "
              f"{len(seen_art):,} distinct art file(s) referenced by a map")
        orphans = on_disk - set(seen_art) - claimed
        print(f"  {len(orphans):,} PNG(s) on disk that no map and no index entry names")

    # ---- who contributed what ------------------------------------------------------------------
    contributed = collections.Counter()
    shared = collections.Counter()
    for record in locations.values():
        came_from = record.get("from") or []
        for one in came_from:
            contributed[one] += 1
        if len(came_from) > 1:
            for one in came_from:
                shared[one] += 1
    if contributed:
        print(f"\n{'versions':>9}{'shared':>8}  profile")
        for profile, count in contributed.most_common():
            print(f"{count:>9,}{shared[profile]:>8,}  {profile}")

    print()
    if problems:
        for line in problems[:20]:
            print(f"  FAIL  {line}")
        print(f"\n{len(problems)} problem(s)")
        return 1
    print("every check passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
