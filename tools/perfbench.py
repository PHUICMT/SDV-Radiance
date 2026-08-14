"""Measure what Radiance actually costs, in the conditions the lag reports describe.

Every perf claim on the branch so far comes from reading the source or from the 1.5.0
benchmark table, and the reports contradict both: nuupon lags on an RTX 3080 (so it is not
fill rate), only in DAYTIME (so it is not the light count), and sudienkhung1 lags with the
features switched off (so it is not behind a gate). None of that has been measured on the
current build even once.

    python tools/perfbench.py                 all configs, all scenes
    python tools/perfbench.py --config full   one config only

For each config the run writes config.json, starts the game, loads the first save, walks the
scene list, and keeps the radiance_report FrameCost block from each stop. Results land in
docs/local/perf/bench-<date>/ as one .txt per (config, scene) plus a summary table.

The player's own config.json is restored in a finally block. A run that dies half way leaves
the game killed and the config back where it started - see multidump.py, where skipping that
left two instances fighting over the bridge port.
"""
import argparse, json, os, re, shutil, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
CONFIG = os.path.join(GAME, "Mods", "03_Graphics-FX", "SDV-Radiance", "config.json")
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
REPORT = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Dumps", "radiance-report.txt")
OUT = os.path.join(REPO, "docs", "local", "perf", "bench-" + time.strftime("%Y-%m-%d"))

# The FrameCost window is 300 frames. Anything shorter reports a partly-empty average, which
# reads as the scene being cheap rather than as the sample being short.
SETTLE_SECONDS = 7

# Each stop reproduces one thing a reporter described, so a number here can be pointed at a
# comment. The daytime/night pair on the same map is the whole point: the reports say daytime
# is the expensive half, which is backwards for a lighting mod and is the claim to test first.
SCENES = [
    ("farm-day",     "Farm",     64, 15, 1200),   # N1Zenma: near crops and structures, daytime
    ("town-day",     "Town",     45, 55, 1200),   # nuupon: big map, daytime
    ("town-night",   "Town",     45, 55, 2200),   # the 1.5.0 baseline spot, for comparison
    ("beach-day",    "Beach",    30, 30, 1200),   # water on screen
    ("indoor",       "SeedShop",  5, 18, 1200),   # nuupon: small maps do not lag
]

# Every switch the tuner exposes, off. Enabled stays true because the question is what the mod
# still costs when the player has turned the features off - which is sudienkhung1's exact case,
# and the one the audit has no answer for.
ALL_OFF = {
    "BloomEnabled": False, "ColorGradeEnabled": False, "GodRaysEnabled": False,
    "GodRaysSun": False, "FogEnabled": False, "FogNightMist": False,
    "CloudShadowEnabled": False, "TiltShiftEnabled": False, "VignetteEnabled": False,
    "ChromaticAberrationEnabled": False, "WaterEnabled": False, "WaterReflection": False,
    "LightingEnabled": False, "FloodLightingEnabled": False, "LightingShadows": False,
    "DirectionalShadowsEnabled": False, "DirectionalShadowObjects": False,
    "WindowEffectsEnabled": False, "WindowBeamEnabled": False, "BlueLightFilter": 0.0,
}

CONFIGS = {
    # As the player has it. The number every other row is measured against.
    "full": {},
    # Everything on, blur off. DrawSoft draws the sprite ONCE at blur 0 and nine times otherwise,
    # so the gap between this row and `full` is the tap cost and nothing else - no settings
    # changed, no features removed, no guessing which of the two the milliseconds belong to.
    "noblur": {"DirectionalShadowBlur": 0.0},
    # Object shadows off, everything else as the player has it. This is the switch the reports
    # keep landing on ("fine with the setting off"), so it is worth its own row.
    "noobjects": {"DirectionalShadowObjects": False},
    # Features off, mod loaded. Whatever this still costs is ungated cost, and ungated cost is
    # the only kind that can explain "I turned everything off and it still lags".
    "off": ALL_OFF,
    # The floor. Harmony patches are installed at startup whatever this says, so this is not a
    # true "mod absent" reading - it is "mod present, drawing nothing", which is the comparison
    # that isolates the patches themselves.
    "disabled": dict(ALL_OFF, Enabled=False),
}


def rpc(tool, args=None, timeout=1800, tries=60):
    """One bridge call, retried while the game's main thread is still loading mods."""
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


def bridge_down(deadline=60):
    t0 = time.time()
    while time.time() - t0 < deadline:
        try:
            port = int(open(PORT_FILE).read().strip())
            urllib.request.urlopen(urllib.request.Request(
                f"http://127.0.0.1:{port}/rpc", data=b'{"tool":"ping"}',
                headers={"Content-Type": "application/json"}), timeout=3)
            time.sleep(2)
        except Exception:
            return True
    return False


def wait_bridge(deadline=420):
    t0 = time.time()
    while time.time() - t0 < deadline:
        try:
            rpc("ping", timeout=5, tries=1)
            return True
        except Exception:
            time.sleep(3)
    return False


def write_config(base, overrides):
    doc = dict(base)
    doc.update(overrides)
    json.dump(doc, open(CONFIG, "w", encoding="utf-8"), ensure_ascii=False, indent=2)


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


# FrameCost.Describe writes "  <name padded to 26>  avg  0.412 ms   worst  1.900 ms".
COST_LINE = re.compile(r"^\s+(\S.*?)\s+avg\s+([\d.]+) ms\s+worst\s+([\d.]+) ms\s*$", re.M)


def costs_from_report(text):
    """Every measured part except the TOTAL row, which is summed here instead.

    Keeping the report's own TOTAL would double every figure in the table, and a wrong total
    that looks plausible is worse than no total at all.

    None means the report carried NO measurement, which is what Enabled=false produces: the
    per-frame handler returns before the window ever advances. That is not a zero-cost reading
    and must not be tabled as one - a row of 0.000 there reads as "measured, free", which is the
    opposite of "not measured".
    """
    if "no frames measured yet" in text:
        return None
    out = {}
    for name, avg, mx in COST_LINE.findall(text):
        name = name.strip()
        if name and name != "TOTAL":
            out[name] = (float(avg), float(mx))
    return out


# The work-done block added alongside the timings: "  object sprite bakes  avg    2.1   worst  14"
CHURN_LINE = re.compile(r"^\s+(\S.*?)\s+avg\s+([\d.]+)\s+worst\s+(\d+)\s*$", re.M)
CACHE_LINE = re.compile(r"^\s+(object|character) bake cache\s+(\d+) of (\d+) slots\s*$", re.M)
# "  WHOLE FRAME (wall clock)   avg 21.418 ms   worst 40.112 ms   =  46.7 fps" + the share line
FRAME_LINE = re.compile(
    r"WHOLE FRAME \(wall clock\)\s+avg\s+([\d.]+) ms\s+worst\s+([\d.]+) ms\s+=\s+([\d.]+) fps"
    r"\s*\n\s*\.\.\.of which measured above\s+([\d.]+)%", re.M)


def churn_from_report(text):
    """One line pairing bake churn with cache occupancy - the pair is the whole diagnosis.

    High bakes with a cache well under its cap is a scene that keeps changing; the same bakes
    with the cache pinned at the cap is thrash, and only the second one is ours to fix.
    """
    bits = [f"{n.strip()}={avg}/f (worst {mx})" for n, avg, mx in CHURN_LINE.findall(text)
            if float(avg) > 0 or int(mx) > 0]
    bits += [f"{kind} cache {n}/{cap}" for kind, n, cap in CACHE_LINE.findall(text)]
    return "  ".join(bits)


def measure(cfg_name, base):
    kill_game()
    bridge_down()
    write_config(base, CONFIGS[cfg_name])
    print(f"\n{'='*70}\nconfig: {cfg_name}\n{'='*70}", flush=True)
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    if not wait_bridge():
        print(f"  bridge never came up for {cfg_name}")
        return {}
    load_save()
    try:
        # Uncapped, or the whole-frame row is 16.67 ms in every config and the benchmark cannot
        # see the GPU cost it exists to find.
        rpc("set", {"pauseInactive": False, "uncapped": True}, timeout=30)
    except Exception:
        pass    # only stops the game pausing while unfocused; not worth failing the run over

    results, frames = {}, {}
    for scene, loc, x, y, tod in SCENES:
        try:
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            # Re-assert uncapped every scene. Asking once at the start is not enough: a run came
            # back with all five scenes at exactly 5.55 ms, which is a refresh cap and not a
            # measurement, because something between the warps had put the limiter back.
            rpc("set", {"time": tod, "uncapped": True}, timeout=60)
            # Indoors the game only refreshes window glow on ENTER, so a clock change while
            # standing inside leaves the room in its old light. Re-enter after setting it.
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            time.sleep(SETTLE_SECONDS)
            # The bridge hands the command to SMAPI's queue and returns before it has run, so
            # "the call succeeded" says nothing about the file. Wait for the WRITE instead, or
            # every scene after the first reads the previous scene's numbers.
            was = os.path.getmtime(REPORT) if os.path.exists(REPORT) else 0
            rpc("console", {"command": "radiance_report"}, timeout=120)
            for _ in range(40):
                if os.path.exists(REPORT) and os.path.getmtime(REPORT) > was:
                    break
                time.sleep(0.5)
            else:
                raise RuntimeError("radiance_report never wrote a new file")
            time.sleep(0.5)
            text = open(REPORT, encoding="utf-8", errors="replace").read()
        except Exception as e:
            print(f"  {scene}: FAILED {e}", flush=True)
            continue
        os.makedirs(OUT, exist_ok=True)
        shutil.copy2(REPORT, os.path.join(OUT, f"{cfg_name}-{scene}.txt"))
        c = costs_from_report(text)
        if c is None:
            print(f"  {scene:12s} no measurement (the mod never ran a frame)", flush=True)
            continue
        results[scene] = c
        total = sum(v[0] for v in c.values())
        print(f"  {scene:12s} total {total:6.3f} ms   "
              + "  ".join(f"{k.split('(')[0].strip()}={v[0]:.3f}" for k, v in c.items() if v[0] >= 0.02),
              flush=True)
        churn = churn_from_report(text)
        if churn:
            print(f"               {churn}", flush=True)
        m = FRAME_LINE.search(text)
        if m:
            frames[scene] = float(m.group(1))
            print(f"               frame {m.group(1)} ms ({m.group(3)} fps, worst {m.group(2)})"
                  f"  ours = {m.group(4)}%", flush=True)
    kill_game()
    return results, frames


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", action="append", choices=list(CONFIGS))
    args = ap.parse_args()
    wanted = args.config or list(CONFIGS)

    base = json.load(open(CONFIG, encoding="utf-8"))
    backup = CONFIG + ".perfbench-backup"
    shutil.copy2(CONFIG, backup)
    all_results, all_frames = {}, {}
    try:
        for name in wanted:
            all_results[name], all_frames[name] = measure(name, base)
    finally:
        shutil.copy2(backup, CONFIG)
        os.remove(backup)
        kill_game()
        print("\nconfig.json restored, game closed.")

    os.makedirs(OUT, exist_ok=True)
    lines = ["# Radiance frame cost, measured " + time.strftime("%Y-%m-%d %H:%M"), ""]
    parts = sorted({p for r in all_results.values() for c in r.values() for p in c})
    lines.append("| scene | config | " + " | ".join(parts) + " | total |")
    lines.append("|---" * (len(parts) + 3) + "|")
    for scene, *_ in SCENES:
        for name in wanted:
            c = all_results.get(name, {}).get(scene)
            if not c:
                continue
            cells = [f"{c[p][0]:.3f}" if p in c else "-" for p in parts]
            lines.append(f"| {scene} | {name} | " + " | ".join(cells)
                         + f" | {sum(v[0] for v in c.values()):.3f} |")
    # Frame time, and the same frame time with the machine's own drift taken out.
    #
    # Absolute milliseconds cannot be compared BETWEEN runs: two runs an hour apart came back with
    # every scene slower by a quarter of a millisecond, including scenes with no object shadows in
    # them at all, which is the machine and not the change. The indoor scene is the reference
    # because it exercises the same effect chain with almost no object shadows, so a scene's
    # EXCESS over indoor is the part the shadow work is responsible for, and that survives the
    # comparison. Read the excess column; the raw one is there to show its working.
    if any(all_frames.get(n) for n in wanted):
        lines.append("")
        lines.append("## Frame time (wall clock, uncapped)")
        lines.append("")
        # A frame limiter that survived the uncapped request makes every scene identical, and
        # identical numbers look like a careful result rather than like no result. Say so.
        for name in wanted:
            vals = list(all_frames.get(name, {}).values())
            if len(vals) >= 3 and max(vals) - min(vals) < 0.05:
                warn = (f"**{name}: every scene came back within 0.05 ms of {vals[0]:.2f} ms "
                        f"({1000.0/vals[0]:.0f} fps). That is a frame cap, not a measurement - "
                        f"the numbers below say nothing about this config.**")
                lines.append(warn)
                lines.append("")
                print("\n" + warn, flush=True)
        lines.append("| scene | config | frame ms | fps | over indoor |")
        lines.append("|---|---|---|---|---|")
        for scene, *_ in SCENES:
            for name in wanted:
                ms = all_frames.get(name, {}).get(scene)
                if ms is None:
                    continue
                ref = all_frames.get(name, {}).get("indoor")
                over = f"{ms - ref:+.3f}" if ref is not None else "-"
                lines.append(f"| {scene} | {name} | {ms:.3f} | {1000.0/ms:.0f} | {over} |")

    summary = os.path.join(OUT, "summary.md")
    open(summary, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print("\n".join(lines))
    print(f"\nwritten to {summary}")


if __name__ == "__main__":
    main()
