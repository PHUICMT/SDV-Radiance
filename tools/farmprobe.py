"""Start a game on any farm layout and photograph its water, without touching a real save.

Half the water reports name a farm layout nobody here has - "reflections are missing and you can
see the edges, especially on Beach Farm" is the current one, and every save on this machine is
the standard farm. Reading the map file cannot answer it: the complaint is about what the
reflection does at a shoreline, which only exists once the thing is running.

    python tools/farmprobe.py beach                     start Beach Farm, sweep it, save shots
    python tools/farmprobe.py beach --at 80,48 --ab     the same one spot with the mod on, then off

--ab is the house rule made automatic: photograph the suspect spot with the mod running and
again with Enabled=false, from the same tile at the same clock time, so "is this even us" is
answered by two pictures instead of by reading the shader. Turning off SOME stages has given the
wrong answer twice; only the whole mod settles it.

Needs the AgentBridge `newgame` tool. Nothing is written to the Saves folder: the game only
saves when a day ends, and this never ends one.
"""
import json, os, shutil, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
SHOTS = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "shots")
DUMPS = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Dumps")

# A coarse sweep of the whole farm. Beach Farm's ocean runs down the WEST side and along the
# SOUTH, and the west shoreline is the interesting one: it is vertical, and a vertical shoreline
# is the case the mirror's shoreline search (which marches UP a column) has never been looked at
# on. Spots are clamped into the map by the bridge, so overshooting the edge is safe.
SWEEP = [(x, y) for y in (12, 30, 48, 62) for x in (8, 26, 44, 62, 80)]

# Seconds to let a world that says "up" but not "ready" actually draw itself before believing it.
SETTLE_AFTER_UP = 45
# A frame with almost nothing in it. The HUD alone compresses to a few tens of KB, while any real
# Stardew scene is hundreds - so file size separates "photographed the world" from "photographed
# the loading screen" without needing an image library.
MIN_REAL_SHOT_BYTES = 150_000

# Seconds to let the world settle when it came up but never reported fully ready.
SETTLE_AFTER_UP = 45
# A screenshot of a world that has not drawn yet is a flat fill plus the HUD, and PNG compresses
# that to almost nothing. Every real frame of this game is far bigger. Cheaper and more reliable
# than decoding the image, and it catches the exact failure that invalidated the first A/B.
MIN_SHOT_BYTES = 120_000


def rpc(tool, args=None, timeout=900, tries=60):
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


def console(cmd, keep=None, out_dir=None):
    """Run a console command and, if it writes a file, keep a copy under a name we chose."""
    watched = os.path.join(DUMPS, keep) if keep else None
    was = os.path.getmtime(watched) if watched and os.path.exists(watched) else 0
    rpc("console", {"command": cmd}, timeout=120)
    if not watched:
        time.sleep(1.0)
        return None
    for _ in range(40):
        if os.path.exists(watched) and os.path.getmtime(watched) > was:
            break
        time.sleep(0.5)
    else:
        return None
    dst = os.path.join(out_dir, f"{cmd.replace(' ', '_')}.txt")
    shutil.copy2(watched, dst)
    return dst


CONFIG = os.path.join(GAME, "Mods", "03_Graphics-FX", "SDV-Radiance", "config.json")


def run_pass(farm, out, spots, tag, overrides, base):
    """One launch: patch the config, start the farm, photograph every spot, put the config back."""
    if overrides is not None:
        doc = dict(base)
        doc.update(overrides)
        json.dump(doc, open(CONFIG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    kill_game()
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    try:
        if not wait_bridge():
            print("bridge never came up")
            return
        print(rpc("newgame", {"farmType": farm, "farmName": "Probe"}, timeout=60), flush=True)
        # `worldUp` rather than `ready`: a new game draws its farm minutes before SMAPI calls the
        # load finished, and everything this script does (warp, screenshot, console) works the
        # moment the world draws. Waiting for `ready` here meant waiting out the whole NPC build
        # of every content pack on the machine, for nothing.
        t0, up_at = time.time(), None
        while time.time() - t0 < 900:
            st = rpc("state", timeout=30).get("result", {})
            if st.get("ready"):
                print(f"ready after {time.time()-t0:.0f}s: {st.get('location')}", flush=True)
                break
            # worldUp flips the instant the game mode changes, which is BEFORE the first frame is
            # drawn: warping and shooting on that edge produced a photograph of a white screen
            # with only the HUD on it, and a blank A/B frame reads as "the mod was the problem".
            # So it is a fallback with a settle, not a green light.
            if st.get("worldUp"):
                up_at = up_at or time.time()
                if time.time() - up_at > SETTLE_AFTER_UP:
                    print(f"world up (not fully ready) after {time.time()-t0:.0f}s", flush=True)
                    break
            time.sleep(5)
        else:
            print("new game never came up")
            return
        try:
            rpc("set", {"pauseInactive": False, "time": 1200}, timeout=30)
        except Exception:
            pass

        for x, y in spots:
            try:
                rpc("goto", {"location": "Farm", "x": x, "y": y}, timeout=120)
                time.sleep(2.5)
                # Retry a shot that came out empty rather than filing it. A blank frame in an A/B
                # is worse than a missing one: it looks like a result.
                for attempt in range(6):
                    shot = rpc("screenshot", {"scale": 1}, timeout=120).get("result", {}).get("path")
                    if shot and os.path.exists(shot) and os.path.getsize(shot) >= MIN_REAL_SHOT_BYTES:
                        break
                    time.sleep(5)
                if not (shot and os.path.exists(shot)):
                    print(f"  {x},{y} no shot", flush=True)
                    continue
                size = os.path.getsize(shot)
                shutil.copy2(shot, os.path.join(out, f"{tag}-{x:03d}-{y:03d}.png"))
                print(f"  {x},{y} shot ok ({size//1024} KB)"
                      + ("  LOOKS BLANK - world had not drawn" if size < MIN_REAL_SHOT_BYTES else ""),
                      flush=True)
            except Exception as e:
                print(f"  {x},{y} FAILED {e}", flush=True)

        # The two that speak directly to the complaint: march lists tiles that ripple but show no
        # reflection, verify scores the mask against the labels. Taken from the last swept spot.
        for cmd, keep in (("radiance_march", None), ("radiance_verify", None),
                          ("radiance_report", "radiance-report.txt")):
            try:
                p = console(cmd, keep, out)
                print(f"  {cmd} -> {p or 'console only, see SMAPI log'}", flush=True)
            except Exception as e:
                print(f"  {cmd} FAILED {e}", flush=True)
    finally:
        kill_game()


def main():
    farm = sys.argv[1] if len(sys.argv) > 1 else "beach"
    out = os.path.join(REPO, "docs", "local", "perf", f"farmprobe-{farm}-" + time.strftime("%Y-%m-%d"))
    os.makedirs(out, exist_ok=True)

    spots = SWEEP
    if "--at" in sys.argv:
        x, y = sys.argv[sys.argv.index("--at") + 1].split(",")
        spots = [(int(x), int(y))]

    base = json.load(open(CONFIG, encoding="utf-8"))
    try:
        if "--ab" in sys.argv:
            # Three arms, because two were not enough. "Whole mod off" is the house rule and stays
            # first, but on a NEW game it photographs a white screen - the world has not drawn and
            # a blank frame in an A/B reads as a result. So the water arm is carried alongside it:
            # if the shoreline rim is gone with only the water stage off, it is ours, and that
            # holds whether or not the whole-mod frame came out usable.
            for tag, over in (("on", base), ("nowater", {"WaterEnabled": False, "WaterReflection": False}),
                              ("off", {"Enabled": False})):
                print(f"--- {tag}", flush=True)
                run_pass(farm, out, spots, tag, over, base)
        else:
            run_pass(farm, out, spots, "farm", None, base)
    finally:
        # The config is the player's own file. Putting it back is not optional, and a crash in
        # the middle of a pass is exactly when it would otherwise be left switched off.
        json.dump(base, open(CONFIG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"\nwritten to {out}")


if __name__ == "__main__":
    main()
