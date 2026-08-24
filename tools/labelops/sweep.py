"""Dump every mod profile in turn into one cumulative maps.json.

A location only reaches the dump if its mod is loaded, and map mods that replace the same
target cannot be loaded together, so full coverage needs a pass per profile. Tilesheet ART
needs no pass at all: the dump reads every PNG off disk, Mods (disabled) included.

    python sweep.py                    Label-BaseArt, then the whole MapPass colouring
    python sweep.py MapPass-01 ...     just these
    python sweep.py --list             what would run, and what is already done
    python sweep.py --redo             run them even if the dump already has them
    python sweep.py --no-sheets        skip the disk sweep for art no map places

WHAT CHANGED FROM multidump.py, and why this one is shorter:

  * No merge step. The dump accumulates: it reads the maps.json already there, keeps every
    version of every location, and adds this profile's. multidump merged afterwards in python,
    writing a v1 document that threw away the per-map art the merge existed to preserve.
  * No per-profile copy of maps.json. Those were snapshots of a file that only grew; thirty of
    them was most of a hundred gigabytes for a history nobody read.
  * RESUMABLE. maps.json lists the profiles that went into it, so a run that dies at pass 19
    picks up at 19. That is the difference between a three-hour job you can interrupt and one
    you cannot.

The game is killed and restarted per profile because mods are read once at startup.
"""
import argparse, json, os, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
PROFILE_DIR = os.path.join(GAME, "mod-profiles")
PYTHON = r"D:\Program\anaconda3\envs\ml\python.exe"
APPLY = os.path.join(REPO, "tools", "labelops", "applyprofile.py")
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
LOG_DIR = os.path.join(HFDIR, "sweep-logs")

# Set when something is wrong with the PLAN rather than with one pass, so the caller stops
# instead of repeating the same mistake another eighty-six times.
ABORT_REASON = ""


# ---- the bridge ------------------------------------------------------------------------------

def rpc(tool, args=None, timeout=1800, tries=60, reconnects=20):
    """One bridge call, retried while the game's MAIN THREAD is still busy.

    The bridge answers `ping` from its listener thread, so "the bridge is up" says nothing about
    the game being able to RUN anything: with a hundred mods the main thread stays busy loading
    for a minute and every queued job comes back 500 "main-thread job timed out". Treating that
    as fatal is what once failed three passes whose profiles were perfectly fine.

    THE PORT IS RE-READ EVERY ATTEMPT, because it is not a constant. The bridge walks
    5757, 8757-8759, 47600-47601 and publishes whichever it could take, so a game launched while
    the last one still holds 5757 comes up on a different port and rewrites port.txt underneath a
    caller that read it once. Four passes of sixteen died that way in one run: the file still
    named the port of a game that was shutting down, which answered the ping and then dropped the
    connection mid-call (WinError 10054), or had already gone (10061). Neither is a bad profile,
    and neither should end a pass.
    """
    body = json.dumps({"tool": tool, "args": args or {}}).encode()
    last = ""
    dropped = 0
    for _ in range(tries):
        try:
            port = int(open(PORT_FILE).read().strip())
        except (OSError, ValueError):
            # No published port means no bridge to call yet, which is a wait and not a failure.
            dropped += 1
            if dropped > reconnects:
                raise RuntimeError(f"{tool}: no bridge port was ever published")
            time.sleep(3)
            continue
        request = urllib.request.Request(f"http://127.0.0.1:{port}/rpc", data=body,
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            last = error.read().decode("utf-8", errors="replace")
            if "main-thread job timed out" in last:
                time.sleep(5)
                continue
            raise RuntimeError(f"{tool}: HTTP {error.code} {last[:300]}")
        except (urllib.error.URLError, OSError) as error:
            # Refused or reset: the port we dialled is not the bridge any more. Wait for the
            # file to name a live one rather than calling the pass lost.
            last = str(error)
            dropped += 1
            if dropped > reconnects:
                raise RuntimeError(f"{tool}: the bridge never answered ({last[:200]})")
            time.sleep(3)
    raise RuntimeError(f"{tool}: the main thread never freed up ({last[:200]})")


def wait_for_bridge(deadline=420):
    started = time.time()
    while time.time() - started < deadline:
        try:
            rpc("ping", timeout=5, tries=1)
            return True
        except Exception:
            time.sleep(3)
    return False


def wait_for_bridge_gone(deadline=60):
    """Until the OLD bridge is really down, or the next pass talks to a dying game."""
    started = time.time()
    while time.time() - started < deadline:
        try:
            port = int(open(PORT_FILE).read().strip())
            urllib.request.urlopen(urllib.request.Request(
                f"http://127.0.0.1:{port}/rpc", data=b'{"tool":"ping"}',
                headers={"Content-Type": "application/json"}), timeout=3)
            time.sleep(2)
        except Exception:
            return True
    return False


def game_is_running():
    listed = subprocess.run(["tasklist", "/FI", "IMAGENAME eq StardewModdingAPI.exe"],
                            capture_output=True, text=True, errors="replace")
    return "StardewModdingAPI.exe" in (listed.stdout or "")


def kill_game(deadline=90):
    """Kill it, WAIT for it to be gone, and unpublish the port it was answering on.

    A fixed four-second sleep was not waiting: the process outlives the taskkill by longer than
    that with two thousand mod folders loaded, and while it lives it still holds its port and
    still answers a ping. The next pass then read port.txt, found a port that a dying game was
    still listening on, and lost the pass the moment it died.

    Deleting port.txt is what closes that door for good. The file can only be written by a bridge
    that has just bound a port, so once it is gone there is no stale number left to dial, and the
    wait below becomes a wait for the NEW game rather than a hopeful ping at the old one. This
    matters more than it looks: the bridge does not always get the same port. It walks 5757,
    8757-8759, 47600-47601 and takes the first one free, so a launch that overlaps a shutdown
    comes up somewhere else entirely.
    """
    subprocess.run(["taskkill", "/F", "/IM", "StardewModdingAPI.exe"], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "Stardew Valley.exe"], capture_output=True)
    started = time.time()
    while game_is_running() and time.time() - started < deadline:
        time.sleep(2)
    time.sleep(2)                       # the sockets outlive the process by a moment
    try:
        os.remove(PORT_FILE)
    except OSError:
        pass


def load_save_and_dump(profile, all_sheets):
    """Load the first save if the game is not in one, then dump this profile's maps."""
    state = rpc("state", timeout=30).get("result", {})
    if not state.get("ready"):
        saves = rpc("load").get("result", {}).get("saves", [])
        if not saves:
            raise RuntimeError("no saves found")
        rpc("load", {"save": saves[0]})
        started = time.time()
        while time.time() - started < 900:
            state = rpc("state", timeout=30).get("result", {})
            if state.get("ready"):
                break
            time.sleep(5)
        if not state.get("ready"):
            raise RuntimeError("the save never finished loading")
    try:
        rpc("set", {"pauseInactive": False}, timeout=30)
    except Exception:
        pass          # not fatal: it only stops the game pausing while it is unfocused
    clear_the_screen()
    step_off_a_festival()
    return rpc("dump", {"all": all_sheets, "profile": profile})


def step_off_a_festival():
    """A festival owns its location and fills it with props that exist one day a year.

    Nothing here can sleep, so the way past one is to stop being on that day: the date is set
    directly, which skips the overnight logic entirely - nothing grows, spoils or ships because
    a dump wanted a different calendar. Up to four days are tried, since a mod-added festival
    can sit next to a vanilla one.

    Every pass loads the same save, so if this cannot be resolved it is not a bad pass, it is a
    bad plan, and the whole run stops rather than repeating it a hundred more times.
    """
    global ABORT_REASON
    for _ in range(4):
        state = rpc("state", timeout=30).get("result", {})
        if not state.get("festival"):
            return state
        day = int(state.get("dayOfMonth") or 1)
        nextday = day % 28 + 1
        print(f"    a festival is running on day {day}; moving to day {nextday}", flush=True)
        rpc("set", {"day": nextday}, timeout=30)
        clear_the_screen()
    ABORT_REASON = ("a festival is still running after moving four days on, which means the "
                    "date is not what is holding it. Every pass would hit the same thing.")
    raise RuntimeError(ABORT_REASON)


def clear_the_screen():
    """Skip whatever took the screen when the save came up, and say what could not be skipped.

    Mods ship intro letters, tutorial popups and arrival events, and a pass that loads into one
    is a pass waiting on a cutscene. Tried twice: skipping an event often lands on the menu that
    the event was holding back.
    """
    for attempt in range(2):
        state = rpc("state", timeout=30).get("result", {})
        if not (state.get("eventUp") or state.get("menu") or state.get("fading")):
            return state
        result = rpc("clear", timeout=60).get("result", {})
        for line in (result.get("did") or []):
            print(f"    {line}", flush=True)
        for line in (result.get("stuck") or []):
            print(f"    ! {line}", flush=True)
        if attempt == 0:
            time.sleep(2)
    return rpc("state", timeout=30).get("result", {})


# ---- what went wrong, if anything did --------------------------------------------------------

def mods_smapi_refused():
    """Mods SMAPI would not load this run.

    A pack that refused to install still lets the game start and the dump succeed - it just
    quietly contributes no maps, which looks identical to a good pass until someone counts.
    SMAPI writes SMAPI-latest.player-2.txt when a second instance is up, so take the NEWEST
    file rather than the obvious name.
    """
    folder = os.path.expanduser(r"~\AppData\Roaming\StardewValley\ErrorLogs")
    try:
        logs = [os.path.join(folder, f) for f in os.listdir(folder)
                if f.startswith("SMAPI-latest") and f.endswith(".txt")]
        text = open(max(logs, key=os.path.getmtime), encoding="utf-8", errors="replace").read()
    except Exception:
        return []
    bad = []
    for line in text.splitlines():
        if "ERROR SMAPI" in line and ("skipped" in line.lower() or "aren't installed" in line
                                      or "installed incorrectly" in line):
            bad.append(line.strip()[:160])
        elif line.strip().startswith("- ") and "because it requires mods" in line:
            bad.append(line.strip()[:160])
    return bad[:12]


# ---- the dump as it stands -------------------------------------------------------------------

def dump_state():
    """(profiles already in it, how many location versions it holds). Empty when there is none."""
    path = os.path.join(HFDIR, "maps.json")
    if not os.path.exists(path):
        return set(), 0
    try:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)
    except Exception:
        return set(), 0
    return set(document.get("profiles") or []), len(document.get("locations") or {})


def snapshot(name):
    """Save whatever is enabled right now, so the run can be undone."""
    enabled = {}
    base = os.path.join(GAME, "Mods")
    for category in sorted(os.listdir(base)):
        path = os.path.join(base, category)
        if not os.path.isdir(path):
            continue
        mods = sorted(m for m in os.listdir(path) if os.path.isdir(os.path.join(path, m)))
        if mods:
            enabled[category] = mods
    document = {"name": name, "created": time.strftime("%Y-%m-%d"),
                "note": "Automatic snapshot taken before a label dump sweep.",
                "enabled": enabled}
    with open(os.path.join(PROFILE_DIR, f"{name}.json"), "w", encoding="utf-8") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=2)
    print(f"snapshot saved as profile '{name}' "
          f"({sum(len(v) for v in enabled.values())} mods)")


def in_pass_order(names):
    """MapPass-9 before MapPass-10. Sorted as text it is the other way round, and past ninety-nine
    the list reads 09, 10, 100, 101, 11 - which is not wrong, only unreadable."""
    def key(name):
        head, _, tail = name.rpartition("-")
        return (head, int(tail)) if tail.isdigit() else (name, 0)
    return sorted(names, key=key)


def known_profiles():
    """Every profile worth sweeping, base art first.

    Label-BaseArt goes first on purpose: whichever profile is dumped first keeps the PLAIN name
    for every location it has, so the base game holds `Town` and a pack that repaints it holds
    `Town~<stamp>`. Dump a map pack first and the base game becomes the variant.
    """
    if not os.path.isdir(PROFILE_DIR):
        return []
    names = [os.path.splitext(f)[0] for f in os.listdir(PROFILE_DIR) if f.endswith(".json")]
    # Label-BaseArt for the base game's own maps, then the MapPass colouring, which is the set
    # that actually guarantees coverage: one colour per profile, no two clashing map mods
    # sharing one. The older Label-Wide profiles predate it and only overlap it.
    solos = in_pass_order(n for n in names if n.startswith("Solo-"))
    batches = in_pass_order(n for n in names if n.startswith("Batch-"))
    redos = in_pass_order(n for n in names if n.startswith("Redo-"))
    wanted = (solos + batches + redos) if (solos or batches or redos) else in_pass_order(
        n for n in names if n.startswith("MapPass-"))
    base = ["Label-BaseArt"] if "Label-BaseArt" in names else []
    return base + wanted


# ---- one pass --------------------------------------------------------------------------------

def run_profile(profile, all_sheets):
    print(f"\n{'=' * 70}\n{profile}"
          + ("   (+ every tilesheet on disk)" if all_sheets else "")
          + f"\n{'=' * 70}", flush=True)
    kill_game()
    wait_for_bridge_gone()
    switched = subprocess.run([PYTHON, APPLY, profile], capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
    print((switched.stdout or switched.stderr).strip(), flush=True)
    if switched.returncode != 0:
        print(f"  profile switch FAILED, skipping {profile}")
        return False
    # The game's console used to share this terminal, so SMAPI's stack traces scrolled through
    # the progress bar and the one line worth watching was never where it was left. It goes to a
    # file per pass, which is also where you want it when a pass does fail.
    os.makedirs(LOG_DIR, exist_ok=True)
    log_path = os.path.join(LOG_DIR, f"{profile}.log")
    game_log = open(log_path, "w", encoding="utf-8", errors="replace")
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME,
                     stdout=game_log, stderr=subprocess.STDOUT)
    started = time.time()
    if not wait_for_bridge():
        print(f"  the bridge never came up for {profile}")
        return False
    try:
        load_save_and_dump(profile, all_sheets)
    except Exception as error:
        print(f"  dump FAILED for {profile}: {error}")
        return False
    # Read the log before trusting the pass, not after.
    for line in mods_smapi_refused():
        print(f"  ! {line}", flush=True)
    done, held = dump_state()
    print(f"  -> {held:,} map version(s) in the dump, {len(done)} profile(s) so far "
          f"({time.time() - started:.0f}s)   log: {os.path.basename(log_path)}", flush=True)
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("profiles", nargs="*", help="profiles to run (default: every Label-/MapPass-)")
    parser.add_argument("--list", action="store_true", help="say what would run and stop")
    parser.add_argument("--redo", action="store_true", help="run profiles the dump already has")
    parser.add_argument("--no-sheets", action="store_true",
                        help="skip the disk sweep for unplaced tilesheet art entirely")
    arguments = parser.parse_args()

    wanted = arguments.profiles or known_profiles()
    if not wanted:
        sys.exit(f"no profiles found in {PROFILE_DIR}")
    done, held = dump_state()
    todo = wanted if arguments.redo else [p for p in wanted if p not in done]

    print(f"dump holds {held:,} map version(s) from {len(done)} profile(s)")
    print(f"{len(wanted)} profile(s) wanted, {len(wanted) - len(todo)} already in, {len(todo)} to run")
    if arguments.list:
        for profile in wanted:
            print(f"  {'done' if profile in done else 'TODO'}  {profile}")
        return
    if not todo:
        print("nothing to do")
        return

    if not os.path.exists(os.path.join(PROFILE_DIR, "_before-sweep.json")):
        snapshot("_before-sweep")

    ran = failed = 0
    for number, profile in enumerate(todo, 1):
        print(f"\n[{number}/{len(todo)}]", end="")
        # The disk sweep for tilesheets NO map places reads every PNG under both mod roots, and
        # what it finds does not depend on which mods are loaded - it reads them off disk either
        # way. Doing it on all thirty passes was the same several minutes of reading, thirty
        # times, for a result identical every time. Once is the whole of it.
        if run_profile(profile, all_sheets=(number == 1 and not arguments.no_sheets)):
            ran += 1
        else:
            failed += 1
    kill_game()
    done, held = dump_state()
    print(f"\n{'=' * 70}\n{ran} profile(s) dumped, {failed} failed. "
          f"{held:,} map version(s) from {len(done)} profile(s) in {os.path.join(HFDIR, 'maps.json')}")
    if failed:
        print("re-run the same command: profiles already in the dump are skipped.")
    print("restore the mod set you had with:  python tools/labelops/applyprofile.py _before-sweep")


if __name__ == "__main__":
    main()
