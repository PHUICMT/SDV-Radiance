"""Answer "is the ground too dark, and is it us" with pixels instead of opinions.

A dark-ground report is the hardest kind to judge by eye, because the honest answer is often
"the game is dark there too". Looking at one screenshot cannot separate the two, and a farm at
night with no lamp nearby genuinely is almost black in vanilla. So this drives the game itself:
one spot, one clock reading, one camera, and a screenshot per config state, then it counts the
pixels. The mod-off shot is the floor every other row is read against.

    python tools/groundprobe.py                       Farm 64 15 at 21:20
    python tools/groundprobe.py --loc Town --x 45 --y 55 --time 2200
    python tools/groundprobe.py --keep-open           leave the game up to look at

Each variant flips ONE switch off, live, via radiance_config, so the ladder runs inside a
single launch. That matters: relaunching per variant reloads the save, and a save reload
changes the weather roll and the NPC positions, which changes the picture for reasons that
have nothing to do with the setting.

Read the table bottom-up. If "disabled" is as black as "baseline", the ground was always that
dark and the mod is not the cause. If exactly one switch brings the light back, that switch
owns the bug.
"""
import argparse, json, os, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
CONFIG = os.path.join(GAME, "Mods", "03_Graphics-FX", "SDV-Radiance", "config.json")
OUT = os.path.join(REPO, "docs", "local", "perf", "ground-" + time.strftime("%Y-%m-%d-%H%M"))

# One switch per row, applied on top of the player's own config and put back before the next.
# Ordered widest first: knowing whether it is the mod at all decides whether the rest matters.
LADDER = [
    ("baseline", {}),
    ("mod off", {"Enabled": False}),
    ("no flood GI", {"FloodLightingEnabled": False}),
    ("no light shadows", {"LightingShadows": False}),
    ("no lighting stage", {"LightingEnabled": False}),
    ("no dir shadows", {"DirectionalShadowsEnabled": False}),
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


def luma_grid(path):
    """Per-pixel luma over the middle of the frame, subsampled.

    The crop is symmetric in both axes so it holds whichever way up the capture lands: the
    toolbar, the clock and the health bars all live at an edge, and a HUD pixel counted as
    scenery is a bright constant that hides exactly the change being looked for.
    """
    from PIL import Image
    with Image.open(path) as im:
        im = im.convert("RGB")
        w, h = im.size
        px = im.load()
        x0, y0 = int(w * 0.12), int(h * 0.25)
        x1, y1 = int(w * 0.72), int(h * 0.75)
        out = []
        for y in range(y0, y1, 2):
            for x in range(x0, x1, 2):
                r, g, b = px[x, y]
                out.append(0.2126 * r + 0.7152 * g + 0.0722 * b)
    return out


def analyse(shots):
    """Compare variants over the pixels the complaint is actually about.

    A whole-frame average cannot answer "is the ground too dark", and reading one as if it
    could gets the sign wrong: this mod raises contrast, so the trees and the roof get
    BRIGHTER while the unlit ground goes to true black, and the two move far enough in
    opposite directions that the frame mean rises while the ground falls. Measured on a farm
    at 21:20 the frame mean said +4.2 luma for the mod and the ground said the opposite.

    So the ground is defined from the picture rather than guessed at: the darkest quarter of
    the mod-off frame is unlit ground almost by construction, and every variant is then read
    over that same fixed set of pixel positions. The camera does not move between shots, so
    the positions stay comparable.
    """
    grids = {name: luma_grid(p) for name, p in shots.items()}
    ref = grids.get("mod off") or next(iter(grids.values()))
    cut = sorted(ref)[len(ref) // 4]
    mask = [i for i, v in enumerate(ref) if v <= cut]
    rows = []
    for name, g in grids.items():
        ground = sum(g[i] for i in mask) / len(mask)
        rows.append((name, sum(g) / len(g), ground,
                     100.0 * sum(1 for i in mask if g[i] < 8) / len(mask)))
    return rows


def fmt(v):
    return str(v).lower() if isinstance(v, bool) else str(v)


def set_keys(keys, original):
    """Apply a variant, or put it back. Restoring reads the player's own config.json rather
    than assuming a default: radiance_config writes nothing to disk, so the file still holds
    what they had, and an earlier version of this that restored every key to 'true' would have
    written the word true into a float the moment the ladder grew a numeric row."""
    for k, v in keys.items():
        val = fmt(v) if original is None else fmt(original.get(k, True))
        rpc("console", {"command": f"radiance_config {k} {val}"}, timeout=60)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--loc", default="Farm")
    ap.add_argument("--x", type=int, default=64)
    ap.add_argument("--y", type=int, default=15)
    ap.add_argument("--time", type=int, default=2120)
    ap.add_argument("--settle", type=float, default=3.0)
    ap.add_argument("--keep-open", action="store_true")
    ap.add_argument("--analyse", metavar="DIR",
                    help="re-read an earlier run's shots instead of driving the game")
    ap.add_argument("--sweep", metavar="Key=v1,v2,v3",
                    help="walk one setting through values instead of the on/off ladder")
    a = ap.parse_args()

    ladder = LADDER
    if a.sweep:
        key, _, vals = a.sweep.partition("=")
        ladder = [("baseline", {}), ("mod off", {"Enabled": False})]
        ladder += [(f"{key}={v}", {key: v}) for v in vals.split(",")]

    if a.analyse:
        shots = {n: os.path.join(a.analyse, n.replace(" ", "-") + ".png") for n, _ in ladder}
        report({n: p for n, p in shots.items() if os.path.exists(p)}, a.analyse)
        return

    config = json.load(open(CONFIG, encoding="utf-8"))
    os.makedirs(OUT, exist_ok=True)
    kill_game()
    print(f"launching the game...", flush=True)
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    if not wait_bridge():
        raise SystemExit("the bridge never came up: is SDV-AgentBridge in the active profile?")
    load_save()
    rpc("set", {"pauseInactive": False}, timeout=30)

    shots = {}
    try:
        for name, keys in ladder:
            # Warp, set the clock, then warp again. Indoors the game only refreshes a room's
            # light on ENTER, so a clock change while standing in it leaves the old light; the
            # second warp costs a second and makes the row mean what it says outdoors too.
            rpc("goto", {"location": a.loc, "x": a.x, "y": a.y}, timeout=120)
            rpc("set", {"time": a.time}, timeout=60)
            rpc("goto", {"location": a.loc, "x": a.x, "y": a.y}, timeout=120)
            set_keys(keys, None)
            time.sleep(a.settle)
            shot = rpc("screenshot", timeout=120).get("result", {}).get("path")
            if not shot or not os.path.exists(shot):
                print(f"  {name:<18} no screenshot came back", flush=True)
                set_keys(keys, config)
                continue
            dest = os.path.join(OUT, name.replace(" ", "-") + ".png")
            with open(shot, "rb") as s, open(dest, "wb") as d:
                d.write(s.read())
            shots[name] = dest
            print(f"  {name:<18} captured", flush=True)
            set_keys(keys, config)
    finally:
        if not a.keep_open:
            kill_game()

    report(shots, OUT)


def report(shots, where):
    if not shots:
        raise SystemExit("nothing captured")
    rows = analyse(shots)
    by = {r[0]: r for r in rows}
    print(f"\n{'variant':<20}{'frame':>9}{'GROUND':>9}{'black':>8}{'vs baseline':>14}")
    b = by.get("baseline")
    for name, frame, ground, black in rows:
        d = "" if not b or name == "baseline" else f"{ground - b[2]:+8.2f}"
        print(f"{name:<20}{frame:>9.2f}{ground:>9.2f}{black:>7.0f}%{d:>14}")

    if not b or "mod off" not in by:
        return
    off = by["mod off"]
    gap = b[2] - off[2]          # positive: the mod LIFTS the ground
    print()
    if gap >= -0.25:
        print(f"VERDICT: the mod is not what blackens the ground here. It sits at {off[2]:.2f} luma "
              f"with the mod OFF and {b[2]:.2f} with it on"
              + (f", so the mod lifts it by {gap:.2f}." if gap > 0.25 else ", the same either way."))
        print(f"         A ground reading in single digits out of 255 is the game's own night, "
              f"not a stage of ours. Reach for LightingNightDarkness, not a bug hunt.")
    else:
        print(f"VERDICT: the mod drops the ground by {-gap:.2f} luma "
              f"({off[2]:.2f} with it off, {b[2]:.2f} with it on).")
        suspects = [r for r in rows if r[0] not in ("baseline", "mod off")
                    and r[2] - b[2] > -gap * 0.4]
        if suspects:
            top = max(suspects, key=lambda r: r[2])
            print(f"         '{top[0]}' puts back {top[2] - b[2]:.2f} of it: that is the suspect.")
        else:
            print("         no single switch puts it back: the cause is not one of these stages.")
    print(f"\nshots in {where}")


if __name__ == "__main__":
    main()
