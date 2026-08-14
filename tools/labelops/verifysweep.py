"""Walk radiance_verify over the places a label change actually touched.

A label merge reports how many tiles moved, which says nothing about whether the mask agrees
with them in the world. This warps to each spot, stands still, runs radiance_verify and keeps
the accuracy line, so a merge can be checked against the picture instead of against its own
tile count.

    python tools/labelops/verifysweep.py
    python tools/labelops/verifysweep.py --spots Beach:30,30 Forest:60,25
    python tools/labelops/verifysweep.py --time 1200 --keep-open

The verify output goes to the SMAPI monitor, not to a file of its own, so it is read back out
of SMAPI-latest.txt by remembering the byte offset before the command and reading from there.
Doing it by size rather than by tailing a fixed number of lines matters: a heavy modded install
writes hundreds of unrelated lines per warp and the accuracy line is not reliably near the end.

STAND STILL is not advice here, it is the measurement: verify says so itself, because a mask
rebuild mid-walk skews mask against labels by a tile and prints paired missing/false ghosts.
The warp lands the farmer and the settle gives the rebuild time to finish before asking.
"""
import argparse, json, os, re, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
LOG = os.path.join(os.path.expanduser("~"), "AppData", "Roaming", "StardewValley",
                   "ErrorLogs", "SMAPI-latest.txt")

# Where the 2026-08-14 merge put its tiles. Each line says which sheet sent us there, because
# a spot that scores badly is only useful if you know which painting it is judging.
SPOTS = [
    ("Beach", 30, 30, "CR_outdoor (crystalinerose BetterWater), *_beach, island_tilesheet_1"),
    ("Forest", 60, 25, "CR_outdoor via Aimon Cindersap"),
    ("Mountain", 46, 5, "mountain lake, the standing baseline"),
    ("Town", 95, 13, "the vanilla bridge baseline"),
    ("IslandSouth", 20, 20, "island_tilesheet_1 (deck)"),
    ("IslandNorth", 30, 30, "island_tilesheet_1 (deck)"),
    ("FarmCave", 5, 5, "ATK_AToMS_INcave (flowing) if the farm ships the WaFF cave"),
]

# The counts are comma-grouped and the last one is followed by a comma when the optional deck
# or hidden clause is present, so the class has to stop on a DIGIT or the comma is captured.
ACC = re.compile(r"\[verify\] accuracy ([\d.]+)%.*?MISSING water ([\d,]*\d), FALSE water ([\d,]*\d)")
HDR = re.compile(r"\[verify\] (\S+): window (\d+x\d+) tiles, ([\d,]+) labeled pixels")
EXTRA = re.compile(r"(deck|hidden|glass) ([\d,]+)")


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


def read_since(offset):
    with open(LOG, "rb") as f:
        f.seek(offset)
        return f.read().decode("utf-8", errors="replace"), f.tell()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spots", nargs="*", help="Location:x,y (default: the merge's own spots)")
    ap.add_argument("--time", type=int, default=1200)
    ap.add_argument("--settle", type=float, default=4.0)
    ap.add_argument("--keep-open", action="store_true")
    a = ap.parse_args()

    spots = SPOTS
    if a.spots:
        spots = []
        for s in a.spots:
            loc, _, xy = s.partition(":")
            x, _, y = xy.partition(",")
            spots.append((loc, int(x or 0), int(y or 0), ""))

    kill_game()
    print("launching the game...", flush=True)
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    if not wait_bridge():
        raise SystemExit("the bridge never came up: is SDV-AgentBridge in the active profile?")
    load_save()
    rpc("set", {"pauseInactive": False}, timeout=30)

    # The bridge returns {count, locations}, NOT {names}. Reading the wrong key gave an empty
    # set, which is falsy, so the "is this location in the save" guard below silently stopped
    # guarding and every spot was warped to regardless. A skip list that never skips is worse
    # than none: it reads as proof the spot existed.
    known = {n.lower() for n in rpc("locations", timeout=60).get("result", {}).get("locations", [])}
    if not known:
        raise SystemExit("the bridge listed no locations: refusing to run a sweep that cannot skip.")
    rows = []
    try:
        for loc, x, y, why in spots:
            if known and loc.lower() not in known:
                print(f"  {loc:<14} not in this save, skipped", flush=True)
                continue
            try:
                rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
                rpc("set", {"time": a.time}, timeout=60)
                rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
                time.sleep(a.settle)
                off = os.path.getsize(LOG)
                rpc("console", {"command": "radiance_verify"}, timeout=120)
                text = ""
                for _ in range(40):
                    time.sleep(0.5)
                    text, _end = read_since(off)
                    if ACC.search(text):
                        break
            except Exception as e:
                print(f"  {loc:<14} FAILED {e}", flush=True)
                continue
            m, h = ACC.search(text), HDR.search(text)
            if not m:
                print(f"  {loc:<14} verify printed no accuracy (no labeled pixels on screen?)", flush=True)
                continue
            acc, miss, false = float(m.group(1)), m.group(2), m.group(3)
            px = h.group(3) if h else "?"
            extra = "  ".join(f"{k} {v}" for k, v in EXTRA.findall(m.group(0)))
            rows.append((loc, acc, px, miss, false, why))
            print(f"  {loc:<14} {acc:6.2f}%   {px:>10} px   missing {miss:>7}  false {false:>7}"
                  + (f"   [{extra}]" if extra else ""), flush=True)
    finally:
        if not a.keep_open:
            kill_game()

    if not rows:
        raise SystemExit("nothing verified")
    print(f"\n{'location':<14}{'accuracy':>10}{'checked px':>13}{'missing':>10}{'false':>10}  what it covers")
    for loc, acc, px, miss, false, why in sorted(rows, key=lambda r: r[1]):
        print(f"{loc:<14}{acc:>9.2f}%{px:>13}{miss:>10}{false:>10}  {why}")
    worst = min(rows, key=lambda r: r[1])
    print(f"\nworst: {worst[0]} at {worst[1]:.2f}%."
          + ("  Nothing here needs a look." if worst[1] >= 99 else
             "  radiance_debug labeldiff there to SEE which tiles disagree."))


if __name__ == "__main__":
    main()
