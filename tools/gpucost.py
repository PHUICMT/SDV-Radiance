"""Price every effect on the GPU's own clock, in the scenes where 1.6.0 actually draws.

The frame-cost table in perfbench times CPU SUBMISSION. That is the wrong side of the fence
for most of what 1.6.0 added: rain, caustics, rings on the water and drops on the glass are
fill and almost nothing else, so they read there as drift. In the 2026-08-20 run, caustics
measured NEGATIVE in five scenes out of six, which is the machine wobbling rather than a cost.

radiance_effectcost runs each effect seven times per frame and keeps the slope, on the GPU
clock. The amplification is also what answers "how would this feel on a slower machine": an
effect drawn seven times is what a card seven times slower pays for drawing it once.

    python tools/gpucost.py                     every scene
    python tools/gpucost.py --scene beach-rain  one scene
    python tools/gpucost.py --amplify 8

Results land in docs/local/perf/gpucost-<date>/ as one .txt per scene plus a summary table.
"""
import argparse, json, os, re, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
SMAPI_LOG = os.path.join(os.environ["APPDATA"], "StardewValley", "ErrorLogs", "SMAPI-latest.txt")
OUT = os.path.join(REPO, "docs", "local", "perf", "gpucost-" + time.strftime("%Y-%m-%d"))

# The scene is the measurement. An effect costs nothing where it draws nothing, so a table of
# these numbers is only honest next to the place they were taken.
SCENES = [
    ("beach-rain", "Beach", 30, 30, 1200, "rain"),   # water + rings + glass + caustics at once
    ("town-storm", "Town",  45, 55, 1200, "storm"),  # the busiest map, plus lightning
    ("town-snow",  "Town",  45, 55, 1200, "snow"),   # flakes rather than streaks
    ("beach-day",  "Beach", 30, 30, 1200, "sun"),    # the dry pair for beach-rain
    ("town-night", "Town",  45, 55, 2200, "sun"),    # the 1.5.0 baseline spot
    ("farm-day",   "Farm",  64, 15, 1200, "sun"),    # crops and trees: where relief and sway live
    # The sky glow only exists on a clear WINTER night outdoors by water, so it can only be
    # priced there. Nothing else in the table needs a season, which is why the season is a
    # per-scene setup command rather than a seventh column on every row.
    ("beach-aurora", "Beach", 30, 30, 2200, "sun", ["debug season winter"]),
]

# "[19:02:24 INFO  SDV-Radiance]   water caustics   0.031 ms", possibly with a trailing note.
# The log prefix has to be part of the pattern: the lines are read out of SMAPI's own file, so
# they never start with the whitespace the console shows. Costs can come out NEGATIVE, which is
# the slope's own noise floor rather than an effect that gives time back, and the sign is kept
# rather than clamped so the floor stays visible in the table.
COST_LINE = re.compile(
    r"^(?:\[[^\]]*\]\s*)?\s+(\S.*?)\s{2,}(-?[\d.]+) ms(\s+\(measured by turning it ON\))?\s*$",
    re.M)
HEADER = re.compile(r"Per-effect GPU cost at .*?amplified slope \(x(\d+)\):")


def rpc(tool, args=None, timeout=600, tries=40):
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
    raise RuntimeError(f"{tool}: gave up ({last[:200]})")


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
    st = rpc("state", timeout=30).get("result", {})
    if st.get("ready"):
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


def log_size():
    return os.path.getsize(SMAPI_LOG) if os.path.exists(SMAPI_LOG) else 0


def read_since(offset):
    with open(SMAPI_LOG, "rb") as f:
        f.seek(offset)
        return f.read().decode("utf-8", errors="replace")


def run_scene(scene, loc, x, y, tod, weather, amplify, setup=()):
    rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
    rpc("set", {"time": tod, "uncapped": True}, timeout=60)
    for command in setup:
        rpc("console", {"command": command}, timeout=60)
    rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
    rpc("console", {"command": "radiance_weather " + weather}, timeout=60)
    time.sleep(4)
    mark = log_size()
    rpc("console", {"command": f"radiance_effectcost {amplify}"}, timeout=120)
    # The run is about four seconds per effect and there are twenty five of them; poll the log
    # for the summary rather than guessing a sleep, because a guess that is short reports an
    # empty table and a guess that is long wastes minutes per scene.
    deadline = time.time() + 420
    while time.time() < deadline:
        text = read_since(mark)
        if HEADER.search(text) and "water in a scene with no water is free" in text:
            return text
        time.sleep(5)
    raise RuntimeError("effectcost never printed a summary")


def costs_from(text):
    start = HEADER.search(text)
    if not start:
        return {}
    body = text[start.end():]
    body = body.split("Amplification is what makes these readable")[0]
    out = {}
    for name, ms, by_on in COST_LINE.findall(body):
        name = re.sub(r"^\[.*?\]\s*", "", name).strip()
        if name:
            out[name] = (float(ms), bool(by_on))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scene", action="append", choices=[s[0] for s in SCENES])
    ap.add_argument("--amplify", type=int, default=8)
    args = ap.parse_args()
    wanted = [s for s in SCENES if not args.scene or s[0] in args.scene]

    kill_game()
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    if not wait_bridge():
        raise SystemExit("bridge never came up")
    load_save()
    try:
        rpc("set", {"pauseInactive": False, "uncapped": True}, timeout=30)
    except Exception:
        pass

    os.makedirs(OUT, exist_ok=True)
    table = {}
    for entry in wanted:
        scene, loc, x, y, tod, weather = entry[:6]
        setup = entry[6] if len(entry) > 6 else ()
        print(f"\n=== {scene} ({loc} {x},{y} {tod} {weather}) ===", flush=True)
        try:
            text = run_scene(scene, loc, x, y, tod, weather, args.amplify, setup)
        except Exception as e:
            print(f"  FAILED {e}", flush=True)
            continue
        open(os.path.join(OUT, scene + ".txt"), "w", encoding="utf-8").write(text)
        table[scene] = costs_from(text)
        for name, (ms, by_on) in sorted(table[scene].items(), key=lambda kv: -kv[1][0]):
            if ms >= 0.005:
                print(f"  {name:<26} {ms:7.3f} ms{'  (by turning it ON)' if by_on else ''}", flush=True)
    kill_game()

    names = sorted({n for c in table.values() for n in c})
    scenes = [s for s in table]
    lines = [f"# Per-effect GPU cost, amplified slope (x{args.amplify + 1}), "
             + time.strftime("%Y-%m-%d %H:%M"), "",
             "Measured on the GPU's own clock. A zero means the effect draws nothing IN THAT",
             "SCENE, not that it is free everywhere.", "",
             "| effect | " + " | ".join(scenes) + " |",
             "|---" * (len(scenes) + 1) + "|"]
    for name in names:
        row = [f"{table[s][name][0]:.3f}" if name in table[s] else "-" for s in scenes]
        lines.append(f"| {name} | " + " | ".join(row) + " |")
    lines += ["", "| TOTAL of the above | "
              + " | ".join(f"{sum(v[0] for v in table[s].values()):.3f}" for s in scenes) + " |"]
    path = os.path.join(OUT, "summary.md")
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print(f"\nwritten to {path}", flush=True)


if __name__ == "__main__":
    main()
