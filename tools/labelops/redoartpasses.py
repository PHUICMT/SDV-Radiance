"""Passes whose dump left art no copy in the corpus can answer, and profiles to run them again.

    python tools/labelops/redoartpasses.py           say which passes and why
    python tools/labelops/redoartpasses.py --write   write Redo- profiles for them

A cell holds (sheet slot, tile index), and an index only means a position once the column count is
known. So art of the wrong WIDTH is not merely a different picture, it is unreadable: every index
lands somewhere else. Art that is too SHORT is the milder case, and art recorded as nothing at all
is the plainest. All three draw black.

WHAT DECIDES A RE-DUMP IS NOT WHICH OF THOSE HAPPENED. A sheet name has many pictures here, and
the labeller binds a fitting one rather than drawing black, so most of those faults cost nothing
but a wrong entry in the record. Ten minutes of sweeping to correct a record nobody reads is ten
minutes wasted. What a re-dump alone can fix is the case where NO copy of that name anywhere in
the corpus fits - the art was never captured, and it cannot be conjured from what was.

This asks blackcells.py that question, so the two tools cannot disagree about what is broken.
An earlier version of this file asked only about width, which queued Color Valley's 84 slots (the
viewer already draws them from another copy) and missed Solo-570 and Batch-57, whose art is short
rather than wide and which between them black out 4,216 cells across two towers of one mod.

Profiles are named Redo- and appended, never rewritten in place: the dump records which pass a
version came from, and rewriting Solo-484 to mean something else would make the record of a
finished pass disagree with the profile of that name. A pass already queued is not queued twice.
"""
import argparse, collections, io, json, os, sys

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twinlabels as labels
import blackcells as black
import gensoloprofiles as solo

PROFILE_DIR = solo.PROFILE_DIR
BASE_PASS = "Label-BaseArt"


def unanswerable(index):
    """pass -> (cells it blacks out, {sheet: versions}), for art no copy in the corpus can hold.

    A map version records every pass that produced it, so one hurt version credits them all: the
    pass that placed the map and the pass that supplied the sheet are both candidates and nothing
    in the dump says which was at fault. Running the lot is cheaper than guessing wrong.
    """
    pool = labels.sheet_versions(index)
    cells = collections.Counter()
    sheets = collections.defaultdict(collections.Counter)
    versions = {}
    for key, entry in index["locations"].items():
        rows = [row for row in black.examine(entry, index, pool)
                if row["verdict"] == "no-art"]
        if not rows:
            continue
        versions[key] = sum(row["cells"] for row in rows)
        for pass_name in (entry.get("from") or []):
            if pass_name == BASE_PASS:
                continue
            for row in rows:
                cells[pass_name] += row["cells"]
                sheets[pass_name][row["sheet"]] += 1
    return cells, sheets, versions


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()

    index = labels.load_index()
    cells, sheets, versions = unanswerable(index)
    record = json.load(io.open(os.path.join(HERE, "passes.json"), encoding="utf-8"))
    known = {entry["name"]: entry for entry in record["passes"]}
    # A pass whose redo is already on the list does not need another one.
    queued = {entry.get("redoOf") for entry in record["passes"] if entry.get("redoOf")}

    print(f"{sum(versions.values()):,} black cell(s) in {len(versions)} map version(s) that no "
          f"copy of the sheet can answer, from {len(cells)} pass(es)")
    print()
    for name, count in cells.most_common():
        entry = known.get(name) or {}
        who = entry.get("mod") or ", ".join(entry.get("mods") or [])
        mark = "already queued" if name in queued else ""
        print(f"  {count:>6} cells  {name:<11} {who[:48]:<48} {mark}")
        print(f"              sheets: {', '.join(sorted(sheets[name]))[:74]}")

    if not arguments.write:
        print("\npass --write to create the Redo- profiles for the ones not yet queued")
        return

    missing = [name for name in cells if name not in known]
    if missing:
        print(f"\nno profile on record for: {', '.join(sorted(missing))}")
    start = 1 + max([int(entry["name"].split("-")[1]) for entry in record["passes"]
                     if entry["name"].startswith("Redo-")] or [0])
    added = []
    for name in sorted(n for n in cells if n in known and n not in queued):
        source = os.path.join(PROFILE_DIR, f"{name}.json")
        if not os.path.exists(source):
            print(f"  profile file missing, skipped: {name}")
            continue
        document = json.load(io.open(source, encoding="utf-8-sig"))
        new_name = f"Redo-{start + len(added):03d}"
        document["name"] = new_name
        document["redoOf"] = name
        document["note"] = (f"{name} again. Its dump left {', '.join(sorted(sheets[name]))} with "
                            f"art no copy in the corpus can index, and {cells[name]} cells draw "
                            f"black because of it. Run it to record the art the game really had.")
        with io.open(os.path.join(PROFILE_DIR, f"{new_name}.json"), "w",
                     encoding="utf-8") as handle:
            json.dump(document, handle, ensure_ascii=False, indent=2)
        added.append({"name": new_name, "kind": document.get("kind", "solo"),
                      "mod": document.get("mod"), "mods": document.get("mods"),
                      "touches": document.get("touches", {}), "redoOf": name,
                      "modCount": sum(len(v) for v in (document.get("enabled") or {}).values())})
    record["passes"].extend(added)
    with io.open(os.path.join(HERE, "passes.json"), "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False, indent=1)
    print(f"\nwrote {len(added)} Redo- profile(s) and extended passes.json")
    print("run them with:  python tools/labelops/run.py --only sweep")


if __name__ == "__main__":
    main()
