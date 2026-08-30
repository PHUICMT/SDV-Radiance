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
    ("farm-day",     "Farm",     64, 15, 1200, None),   # N1Zenma: near crops and structures, daytime
    ("town-day",     "Town",     45, 55, 1200, None),   # nuupon: big map, daytime
    ("town-night",   "Town",     45, 55, 2200, None),   # the 1.5.0 baseline spot, for comparison
    ("beach-day",    "Beach",    30, 30, 1200, None),   # water on screen
    ("indoor",       "SeedShop",  5, 18, 1200, None),   # nuupon: small maps do not lag
]

# 1.6.0 added rain, snow, lightning, wet glass and rings on the water, and NONE of them draw a
# pixel on a clear day. Measuring the release on the list above only would have reported that
# the whole weather block is free, which is true and useless. Weather is set per location
# context, after the warp, by the mod's own command: the game has no debug command for snow.
WEATHER_SCENES = [
    ("beach-rain",   "Beach",    30, 30, 1200, "rain"),   # water + rings + drops on the glass
    ("town-storm",   "Town",     45, 55, 1200, "storm"),  # rain + lightning on the busiest map
    ("town-snow",    "Town",     45, 55, 1200, "snow"),    # flakes instead of streaks
    ("beach-day",    "Beach",    30, 30, 1200, None),      # the dry pair for beach-rain
    ("town-night",   "Town",     45, 55, 2200, None),      # the 1.5.0 baseline spot
    ("indoor",       "SeedShop",  5, 18, 1200, None),      # dust motes are indoors only
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

# Every switch 1.7.0 added, off, plus flood as the GI model. The 1.7.0 rows build on this so
# each measures one feature against the same floor regardless of what the player runs with.
FLOOR_170 = {
    "SpriteReliefEnabled": False, "FoliageSwayEnabled": False, "SheetUpscaleEnabled": False,
    "FloodGiModel": "Flood", "AuroraEnabled": False, "ShootingStarsEnabled": False,
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

    # ---- 1.6.0. Each row turns off exactly one thing this version added, so the gap between
    # it and `full` is that thing's cost and nothing else. Run them against WEATHER_SCENES.
    "no-precip":  {"PrecipitationEnabled": False},
    "no-particles": {"ParticlesEnabled": False},
    "no-windowreflect": {"WindowReflectionEnabled": False},
    "no-lensdrops": {"WetWorldLensDrops": False},
    "no-caustics": {"WaterCausticsEnabled": False},
    "no-lightning": {"LightningEffectsEnabled": False, "LightningBoltsEnabled": False},
    # The rings are shader work inside a stage that was already running, so this row prices the
    # branch rather than the stage: at 0 strength the whole block is still entered.
    "no-rainrings": {"WaterRainRingStrength": 0.0},
    # Everything 1.6.0 added, off together. Against `full` this is the release's whole bill,
    # which is the number the question "did it get slower" actually wants.
    "no-160": {
        "PrecipitationEnabled": False, "ParticlesEnabled": False,
        "WindowReflectionEnabled": False, "WetWorldLensDrops": False,
        "WaterCausticsEnabled": False, "LightningEffectsEnabled": False,
        "LightningBoltsEnabled": False, "WaterRainRingStrength": 0.0,
    },

    # ---- the two presets we tell people to reach for. Written out by value rather than by
    # name so the bench is measuring a known config rather than whatever ApplyPerfPreset does
    # this week, and with RenderScaleAuto OFF: it moves the scale mid-run, which is the right
    # behaviour in a game and useless in a measurement.
    "preset-performance": {
        "RenderScale": 0.5, "RenderScaleAuto": False, "TiltShiftEnabled": False,
        "ChromaticAberrationEnabled": False, "FloodLightingEnabled": False,
        "LightingEnabled": True, "DirectionalShadowObjects": False,
        "ShadowCastsPerCharacter": 1, "WaterReflection": True,
        "WaterReflectReach": 0.5, "WaterReflectFadeRows": 8,
    },
    "preset-lowspec": {
        "RenderScale": 0.5, "RenderScaleAuto": False, "TiltShiftEnabled": False,
        "ChromaticAberrationEnabled": False, "FloodLightingEnabled": False,
        "LightingEnabled": True, "DirectionalShadowObjects": False,
        "ShadowCastsPerCharacter": 1, "GodRaysEnabled": False, "GodRaysSun": False,
        "WaterReflection": True, "WaterReflectReach": 0.2, "WaterReflectFadeRows": 8,
    },
    # Render scale on its own, so the presets' biggest lever can be told apart from their
    # feature cuts. Fill is quadratic in this number and nothing else here is.
    "half-scale": {"RenderScale": 0.5, "RenderScaleAuto": False},

    # ---- 1.7.0. Each row is the FLOOR below plus exactly one feature, not a delta on the
    # player's config: the first run of these was built on {} and measured nothing, because the
    # author's own config already had relief, cascades and sway switched on, so three of the
    # rows were the same file twice. The floor states every 1.7.0 switch, so a row means the
    # same thing whatever config.json holds. Day scene list on purpose - relief and sway live
    # on crops and trees, which the weather list never visits.
    "base-170": dict(FLOOR_170),
    "relief-on": dict(FLOOR_170, SpriteReliefEnabled=True),
    "sway-on": dict(FLOOR_170, FoliageSwayEnabled=True),
    "upscale-on": dict(FLOOR_170, SheetUpscaleEnabled=True),
    # A swap, not a switch: this row prices cascades AGAINST flood.
    "cascades": dict(FLOOR_170, FloodGiModel="Cascades"),
    # The night-sky pair; only town-night can see it.
    "sky-on": dict(FLOOR_170, AuroraEnabled=True, ShootingStarsEnabled=True),
    "all-170": {"SpriteReliefEnabled": True, "FoliageSwayEnabled": True,
                "SheetUpscaleEnabled": True, "FloodGiModel": "Cascades",
                "AuroraEnabled": True, "ShootingStarsEnabled": True},
}

# The 1.6.0 rows are about weather, so they get the weather scene list.
WEATHER_CONFIGS = {"no-precip", "no-particles", "no-windowreflect", "no-lensdrops",
                   "no-caustics", "no-lightning", "no-rainrings", "no-160", "full",
                   # The preset rows share the list so they can be read straight against `full`.
                   # A preset measured on a different set of scenes answers a different question.
                   "preset-performance", "preset-lowspec", "half-scale"}


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


def clear_the_way(rounds=6):
    """Get past whatever is on screen, and prove it with a warp.

    A save does not hand you a farmer standing in a field: it can open on a cutscene, a letter,
    the morning message, or an NPC greeting, and any of those swallows the warp that follows.
    The bench then measures the room it never left and files the numbers under the scene it was
    asked for. Marnie saying hello is what caught this.

    `clear` takes menus and boxes; `debug EndEvent` skips a running event and warns harmlessly
    when there is none. Both are repeated, because an event can hand straight over to a letter.
    The proof is the WARP, not the absence of a complaint: ask to be moved and read back where
    the game says it is.
    """
    for attempt in range(rounds):
        try:
            rpc("clear", timeout=30)
            rpc("console", {"command": "debug EndEvent"}, timeout=60)
            time.sleep(1.5)
            rpc("goto", {"location": "Farm", "x": 64, "y": 17}, timeout=150)
            time.sleep(1.5)
            where = rpc("state", timeout=30).get("result", {}).get("location")
        except Exception as exc:
            print(f"  clearing the way: {exc}", flush=True)
            continue
        if where == "Farm":
            if attempt:
                print(f"  cleared the way after {attempt + 1} rounds", flush=True)
            return True
        print(f"  something is still holding the screen (still in {where}), trying again", flush=True)
    print("  COULD NOT CLEAR THE SCREEN - measurements after this cannot be trusted", flush=True)
    return False


def shake_off_dialogue():
    """Cheap version for between scenes: a greeting can start at any warp."""
    try:
        rpc("clear", timeout=30)
        rpc("console", {"command": "debug EndEvent"}, timeout=60)
    except Exception:
        pass


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
    clear_the_way()
    try:
        # Uncapped, or the whole-frame row is 16.67 ms in every config and the benchmark cannot
        # see the GPU cost it exists to find.
        rpc("set", {"pauseInactive": False, "uncapped": True}, timeout=30)
    except Exception:
        pass    # only stops the game pausing while unfocused; not worth failing the run over

    results, frames = {}, {}
    scenes = WEATHER_SCENES if cfg_name in WEATHER_CONFIGS else SCENES
    for scene, loc, x, y, tod, weather in scenes:
        try:
            # An NPC can start talking the moment you land, and a dialogue box up
            # during a measurement is a different frame from the one being asked about.
            shake_off_dialogue()
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            # Re-assert uncapped every scene. Asking once at the start is not enough: a run came
            # back with all five scenes at exactly 5.55 ms, which is a refresh cap and not a
            # measurement, because something between the warps had put the limiter back.
            rpc("set", {"time": tod, "uncapped": True}, timeout=60)
            # Indoors the game only refreshes window glow on ENTER, so a clock change while
            # standing inside leaves the room in its old light. Re-enter after setting it.
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            # Weather is per location context and absolute, so it is set AFTER the warp and it
            # is set every scene: the previous scene's storm does not follow you to the beach,
            # and a scene that wants a clear sky has to say so rather than inherit one.
            rpc("console", {"command": "radiance_weather " + (weather or "sun")}, timeout=60)
            shake_off_dialogue()
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

    backup = CONFIG + ".perfbench-backup"
    # A backup already here means the LAST run died before its finally block, so config.json
    # is that run's bench config and not the player's. Taking it as the base spreads a
    # switched-off mod through every row, and copying over the backup destroys the only
    # surviving copy of the real settings. That happened on 2026-08-30 and cost the author
    # twenty-one tuned switches: the run reported "the mod never ran a frame" for every scene
    # of every config, which is at least loud, but the settings were already gone by then.
    if os.path.exists(backup):
        print("a previous run did not finish: restoring config.json from its backup first")
        shutil.copy2(backup, CONFIG)
    else:
        shutil.copy2(CONFIG, backup)

    base = json.load(open(CONFIG, encoding="utf-8"))
    if base.get("Enabled") is False:
        raise SystemExit(
            "config.json says Enabled=false, so nothing can be measured from it. "
            "Turn the mod back on, or delete the key to take the shipped default, and re-run.")
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
