"""Watch a running sweep and say only the things worth waking up for.

    python tools/labelops/sweepwatch.py            follow the run, one line per event
    python tools/labelops/sweepwatch.py --once     print the state of the run and exit

The sweep prints a line per pass, but most of what it prints is not a problem. SMAPI reports
every empty category folder as a skipped mod, and a Solo profile leaves eight of them empty, so
every pass carries eight ERROR lines that mean nothing. A watch that shows those shows nothing
useful; a watch that hides all errors hides the one that matters. These are the events:

    STALLED     no pass has finished for longer than the slowest one so far allows. The game
                hangs on a modal dialogue often enough that this is the failure to expect.
    PATCH       Content Patcher could not apply one patch. The map is still dumped, without the
                change that patch was making. Usually an undeclared dependency: the mod's map
                names a tilesheet its manifest never asks for, so nothing put it in the profile.
    REFUSED     SMAPI would not load a real mod (not an empty folder). That pass ran, reported
                success, and is missing the maps that mod exists to provide, which looks
                identical to a good pass until someone counts. Reported in batches: one line per
                refusal turned out to be most of what this printed, and each one on its own says
                nothing that redoprofiles.py will not say better with all of them in hand.
    CRASHED     the game died during a pass.
    EMPTY       a pass contributed no new map version. Often correct - a farm-type mod's patches
                do not apply on a save with another farm type - so it is counted rather than
                shouted about, and only an unbroken run of them is reported.
    RATE        every so often: passes done, versions held, real time per pass, when it will end.

The point of the run is a version of each map attributable to ONE mod, so a pass that quietly
contributed nothing is a hole in exactly what it exists to build. Counting them as they happen
is how tomorrow starts with a list rather than a suspicion.
"""
import argparse, json, os, re, sys, time

sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

HERE = os.path.dirname(os.path.abspath(__file__))
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
LOG_DIR = os.path.join(HFDIR, "sweep-logs")
STATE = os.path.join(HFDIR, "sweep-watch.json")
CONTRIBUTED = re.compile(r"contributed (\d+) new map version")
# SMAPI writes its own tag at the head of every line, so the dash is never at the start of one.
# Missing that made this silent through the very failure it exists to catch: two mods refused for
# missing dependencies, both passes reported success, and neither said anything.
REAL_REFUSAL = re.compile(r"^(?:\[[^\]]*\]\s*)*-\s+(?!\d\d_)(.+?) because ", re.M)
# "Unhandled exception" alone is not a crash. Content Patcher writes it for a single patch it
# could not apply and the game carries on and dumps normally, which cost a false alarm at
# Solo-190. A crash is the game going away.
CRASH = re.compile(r"(The game crashed|Game has ended|SMAPI terminated|FATAL)", re.I)
# One patch failing IS a hole, though a much smaller one than a refused mod: that map is dumped
# without the change the patch was making. Rare - one pass in the first 190 - and usually an
# undeclared dependency, the mod's own map naming a tilesheet its manifest never asks for.
PATCH_FAILED = re.compile(r"Unhandled exception applying patch: (.+?) >")
# The tilesheet a failed patch could not find. Named in the exception, and the whole of what a
# redo needs: whichever mod ships that PNG is what the profile was missing.
MISSING_SHEET = re.compile(r"invalid tilesheet path '([^']+)'")
EMPTY_RUN_TO_REPORT = 12
REFUSALS_TO_REPORT = 10
PATCH_FAILURES_TO_REPORT = 5
RATE_EVERY = 25
POLL_SECONDS = 30
STALL_MULTIPLE = 6
STALL_FLOOR = 900


def read_log(path):
    try:
        with open(path, "rb") as handle:
            return handle.read().replace(b"\x00", b"").decode("utf-8", "replace")
    except OSError:
        return ""


def inspect(name):
    """(new versions or None while unfinished, mods SMAPI really refused, crashed)."""
    text = read_log(os.path.join(LOG_DIR, f"{name}.log"))
    match = CONTRIBUTED.search(text)
    refused = []
    block = text.find("Skipped mods")
    if block >= 0:
        # Category folders are named 00_Frameworks, 09_Buildings and so on, and SMAPI lists every
        # one a profile leaves empty. A refusal that does not start with two digits is a mod.
        for line in text[block:block + 4000].splitlines():
            hit = REAL_REFUSAL.match(line.strip("\r"))
            if hit and "empty folder" not in line:
                # A mod that ships several content packs is named once per pack it could not
                # load, so the same name arrives three times and reads as three problems.
                name = hit.group(1).strip()
                if name not in refused:
                    refused.append(name)
    return (int(match.group(1)) if match else None), refused, bool(CRASH.search(text)),         sorted(set(PATCH_FAILED.findall(text))),         sorted({os.path.basename(p) for p in MISSING_SHEET.findall(text)})


def planned():
    path = os.path.join(HERE, "passes.json")
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as handle:
        return ["Label-BaseArt"] + [p["name"] for p in json.load(handle)["passes"]]


def dump_state():
    path = os.path.join(HFDIR, "maps.json")
    try:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)
        return set(document.get("profiles") or []), len(document.get("locations") or {})
    except (OSError, ValueError):
        return set(), 0


def save(seen, empties, refusals, patches):
    try:
        with open(STATE, "w", encoding="utf-8") as handle:
            json.dump({"seen": sorted(seen), "empty": sorted(empties),
                       "refused": {name: mods for name, mods in refusals},
                       "patchFailed": {name: {"from": who, "wanted": sheets}
                                       for name, who, sheets in patches}}, handle)
    except OSError:
        pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    arguments = parser.parse_args()

    plan = planned()
    started = time.time()
    # Everything already in the dump when this starts is history, not news: a restart should not
    # replay two hundred REFUSED lines that were reported the first time round. --once wants the
    # opposite, a full account of the run so far.
    seen = set() if arguments.once else set(dump_state()[0])
    # What was already done when this started is not part of what it timed. Dividing the elapsed
    # time by every pass in the dump made a restarted watch report seconds per pass near zero and
    # an end time minutes away.
    began_with = len(seen)
    empties, empty_run = [], 0
    refusals = []
    patches = []
    last_finish = time.time()
    slowest = 60.0
    previous_finish = None

    while True:
        done, versions = dump_state()
        fresh = [name for name in plan if name in done and name not in seen]
        for name in fresh:
            seen.add(name)
            now = time.time()
            if previous_finish:
                slowest = max(slowest, now - previous_finish)
            previous_finish = last_finish = now
            new_versions, refused, crashed, patch_failed, missing_sheets = inspect(name)
            if refused:
                refusals.append((name, refused))
                if len(refusals) % REFUSALS_TO_REPORT == 0:
                    recent = ", ".join(n for n, _ in refusals[-REFUSALS_TO_REPORT:])
                    print(f"REFUSED  {len(refusals)} passes so far had a mod SMAPI would not "
                          f"load; the last {REFUSALS_TO_REPORT}: {recent}")
            if crashed:
                print(f"CRASHED  {name}: the game died during this pass")
            if patch_failed:
                patches.append((name, patch_failed, missing_sheets))
                if len(patches) % PATCH_FAILURES_TO_REPORT == 0:
                    # What they all wanted matters more than which mods they were: one file
                    # missing from five profiles is one fix, five unrelated ones are five.
                    files = sorted({f for _, _, sheets in patches for f in sheets})
                    print(f"PATCH    {len(patches)} passes had a patch Content Patcher could not "
                          f"apply; between them they wanted {', '.join(files[:4])}"
                          f"{' ...' if len(files) > 4 else ''}")
            if new_versions == 0:
                empties.append(name)
                empty_run += 1
                if empty_run and empty_run % EMPTY_RUN_TO_REPORT == 0:
                    print(f"EMPTY    {empty_run} passes in a row added no map version "
                          f"(latest {name}); {len(empties)} empty so far")
            elif new_versions:
                empty_run = 0
            if len(seen) % RATE_EVERY == 0:
                timed = len(seen) - began_with
                per = (time.time() - started) / timed if timed else 0
                left = (len(plan) - len(seen)) * per
                print(f"RATE     {len(seen)}/{len(plan)} passes, {versions:,} versions, "
                      + (f"{per:.0f}s per pass, about {left / 3600:.1f}h left, "
                         if timed else "")
                      + f"{len(empties)} empty"
                      + (f" (of {timed} timed here)" if began_with else ""))
            save(seen, empties, refusals, patches)
        if arguments.once:
            print(f"ONCE     {len(seen)}/{len(plan)} passes in the dump, {versions:,} versions, "
                  f"{len(empties)} empty, {len(refusals)} with a refused mod, "
                  f"{len(patches)} with an unapplied patch")
            return
        if len(seen) >= len(plan):
            print(f"DONE     {len(seen)}/{len(plan)} passes, {versions:,} versions, "
                  f"{len(empties)} contributed nothing")
            return
        stall = max(STALL_FLOOR, slowest * STALL_MULTIPLE)
        if time.time() - last_finish > stall:
            print(f"STALLED  no pass has finished for {int(time.time() - last_finish)}s "
                  f"(slowest so far {int(slowest)}s); at {len(seen)}/{len(plan)}")
            last_finish = time.time()
        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
