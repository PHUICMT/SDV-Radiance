"""Drive the game and close the in-game unknowns left by the 2026-08-14 research pass.

Five questions could not be answered from source, and each needs the game running:

  1. What does the flood lightmap rebuild cost NOW? Two figures circulate, 0.22 and 0.25 ms, and
     commit 185da13 shows both are the PRE-gate cost. Nobody measured it after the gate landed.
     This one is honestly measurable with the existing timers, because the flood sweep is CPU work
     and a Stopwatch sees CPU work correctly. The GPU blind spot applies to draw submission.
  2. What is the bake MISS rate in normal play? FrameCost.Counter.BakeMisses has counted it all
     along and nobody has read it. It decides whether the banded-gradient fallback matters at all.
  3. What does the entity mirror cost on a wooded shoreline?
  4. Is GL_ARB_timer_query available, and can this mod actually run one? (radiance_gldiag)
  5. How fast do animated map tiles advance, in ticks? The dump records frame counts but not the
     interval, so the "can a 6-tick cache keep up" question was unanswerable offline.
     (radiance_anim)

Usage:
    python tools/closeunknowns.py
    python tools/closeunknowns.py --keep-open

The command output goes to the SMAPI monitor, so it is read back out of SMAPI-latest.txt by
remembering the byte offset before each command. Doing it by offset rather than tailing N lines
matters: a heavy modded install writes hundreds of unrelated lines per warp and the line wanted is
not reliably near the end.

STAND STILL is part of the measurement for the report, not advice. FrameCost averages over a window
of frames, so the settle below has to be long enough for a complete window to close, and walking
during it would mix a rebuild-heavy sample into a standing one.
"""
import argparse, json, os, re, shutil, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
APPDATA = os.path.join(os.path.expanduser("~"), "AppData", "Roaming", "StardewValley")
LOG = os.path.join(APPDATA, "ErrorLogs", "SMAPI-latest.txt")
# radiance_report writes here, NOT next to the SMAPI log. Guessing this path wrong cost one whole
# run: every spot was visited and measured, and every report was overwritten by the next before
# anything read it, so the sweep reported success having captured nothing.
REPORT = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Dumps", "radiance-report.txt")
OUT = os.path.join(REPO, "docs", "local", "perf", "closeunknowns")

# Where to stand for each question. The report spots deliberately reuse the scenes the 14 Aug
# baseline used, so the numbers can be compared rather than just admired.
REPORT_SPOTS = [
    ("Farm", 64, 15, 1200, "farm midday, the 1.96 ms baseline scene"),
    ("Farm", 64, 15, 700, "farm at 0700, where shadow shear is largest (noon hides shadow faults)"),
    ("Forest", 60, 25, 1200, "wooded shoreline, the worst case for the entity mirror"),
    ("Town", 95, 13, 1200, "the vanilla bridge baseline"),
]
# Maps chosen for animated tiles: the dump says the median map has 2 and the 90th percentile 417,
# so the sample has to include a heavy one or it will report that everything is fine.
ANIM_SPOTS = [
    ("Forest", 60, 25, "vanilla waterfall"),
    ("Mountain", 46, 5, "vanilla lake and waterfall"),
    ("Town", 95, 13, "fountain and the bridge"),
    ("Beach", 30, 30, "surf"),
    ("Custom_Ridgeside_RidgeForest", 40, 40, "the heaviest map in the dump, 2754 animated tiles"),
]


def rpc(tool, args=None, timeout=120, tries=40):
    port = int(open(PORT_FILE).read().strip())
    body = json.dumps({"tool": tool, "args": args or {}}).encode()
    last = ""
    for _ in range(tries):
        req = urllib.request.Request(f"http://127.0.0.1:{port}/rpc", data=body,
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            last = e.read().decode("utf-8", errors="replace")
            if "main-thread job timed out" in last:
                time.sleep(5)
                continue
            raise RuntimeError(f"{tool}: {last[:300]}")
        except Exception as e:
            last = str(e)
            time.sleep(2)
    raise RuntimeError(f"{tool}: gave up after {tries} tries ({last[:200]})")


def kill_game():
    subprocess.run(["taskkill", "/F", "/IM", "StardewModdingAPI.exe"], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "Stardew Valley.exe"], capture_output=True)
    time.sleep(4)


def wait_bridge(deadline=420):
    t0 = time.time()
    while time.time() - t0 < deadline:
        try:
            rpc("ping", timeout=5, tries=1)
            return True
        except Exception:
            time.sleep(3)
    return False


def load_save():
    if rpc("state", timeout=30).get("result", {}).get("ready"):
        return
    saves = rpc("load").get("result", {}).get("saves", [])
    if not saves:
        raise RuntimeError("no saves found")
    rpc("load", {"save": saves[0]})
    t0 = time.time()
    while time.time() - t0 < 900:
        if rpc("state", timeout=30).get("result", {}).get("ready"):
            return
        time.sleep(5)
    raise RuntimeError("save never finished loading")


def run_capture(command, wait=3.0, want=None, tries=30):
    """Run a console command and return only the log text it produced."""
    off = os.path.getsize(LOG)
    rpc("console", {"command": command}, timeout=180)
    text = ""
    for _ in range(tries):
        time.sleep(wait / 3.0)
        with open(LOG, "rb") as f:
            f.seek(off)
            text = f.read().decode("utf-8", errors="replace")
        if want is None or re.search(want, text):
            break
    return text


def keep(lines, pattern):
    return "\n".join(l for l in lines.splitlines() if re.search(pattern, l))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep-open", action="store_true")
    ap.add_argument("--only", choices=["gl", "anim", "report"], default=None,
                    help="run one section; the others already answered their question")
    ap.add_argument("--settle", type=float, default=12.0,
                    help="seconds to stand still before asking for a report; must exceed one FrameCost window")
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)

    kill_game()
    print("launching the game...", flush=True)
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    if not wait_bridge():
        raise SystemExit("the bridge never came up: is SDV-AgentBridge in the active profile?")
    load_save()
    rpc("set", {"pauseInactive": False}, timeout=30)

    known = {n.lower() for n in rpc("locations", timeout=60).get("result", {}).get("locations", [])}
    if not known:
        raise SystemExit("the bridge listed no locations: refusing to run a sweep that cannot skip.")

    results = {}
    try:
        # ---- Q4: the graphics backend and whether a GPU timer query works here ----
        if a.only in (None, "gl"):
         print("\n== radiance_gldiag ==", flush=True)
         txt = run_capture("radiance_gldiag", want=r"\[gldiag\] (PASS|FAIL)")
         gl = keep(txt, r"\[gldiag\]")
         print(gl or "  (no [gldiag] lines: is the deployed build the one just built?)", flush=True)
         results["gldiag"] = gl

        # ---- Q5: how fast do animated tiles actually advance ----
        print("\n== radiance_anim ==", flush=True)
        anim = []
        for loc, x, y, why in ANIM_SPOTS:
            if loc.lower() not in known:
                print(f"  {loc:<34} not in this save, skipped", flush=True)
                continue
            try:
                rpc("goto", {"location": loc, "x": x, "y": y}, timeout=150)
                time.sleep(2.5)
                txt = run_capture("radiance_anim", want=r"\[anim\]")
            except Exception as e:
                print(f"  {loc:<34} FAILED {e}", flush=True)
                continue
            block = keep(txt, r"\[anim\]")
            anim.append(f"--- {loc} ({why}) ---\n{block}")
            print(f"  {loc:<34} {why}", flush=True)
            for line in block.splitlines():
                print("      " + line.split("] ", 1)[-1], flush=True)
        results["anim"] = "\n\n".join(anim)

        # ---- Q1/Q2/Q3: the cost lines nobody has read ----
        print("\n== radiance_report ==", flush=True)
        reports = []
        for loc, x, y, when, why in (REPORT_SPOTS if a.only in (None, "report") else []):
            if loc.lower() not in known:
                print(f"  {loc:<10} not in this save, skipped", flush=True)
                continue
            try:
                rpc("goto", {"location": loc, "x": x, "y": y}, timeout=150)
                rpc("set", {"time": when}, timeout=60)
                # Re-enter: the game only refreshes a room's window glows on ENTRY, so winding the
                # clock while standing inside leaves the lighting at its old state.
                rpc("goto", {"location": loc, "x": x, "y": y}, timeout=150)
                time.sleep(a.settle)
                run_capture("radiance_report", wait=2.0, want=r"report", tries=10)
                time.sleep(1.5)
                body = open(REPORT, encoding="utf-8", errors="replace").read() if os.path.exists(REPORT) else ""
            except Exception as e:
                print(f"  {loc:<10} FAILED {e}", flush=True)
                continue
            tag = f"{loc}-{when:04d}"
            if body:
                shutil.copyfile(REPORT, os.path.join(OUT, f"report-{tag}.txt"))
            reports.append(f"--- {loc} at {when:04d} ({why}) ---\n" + body)
            wanted = keep(body, r"grid: flood|water entity mirror|water scenery mirror|bake misses|"
                                r"shadow bakes|shadow draw|frame time|worst|avg ")
            print(f"  {loc} {when:04d}  {why}", flush=True)
            for line in wanted.splitlines()[:14]:
                print("      " + line.strip(), flush=True)
        results["reports"] = "\n\n".join(reports)
    finally:
        if not a.keep_open:
            kill_game()

    stamp = os.path.join(OUT, "summary.txt")
    with open(stamp, "w", encoding="utf-8") as f:
        for k, v in results.items():
            f.write(f"=============== {k} ===============\n{v}\n\n")
    print(f"\nwritten: {stamp}", flush=True)


if __name__ == "__main__":
    main()
