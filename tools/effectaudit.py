"""Measure what EVERY effect costs, one at a time, inside a single game run.

The per-config benchmark (perfbench.py) restarts the game per configuration, and restarts move
the machine's baseline by ~0.25 ms in every scene - more than most single effects cost, so a
per-effect table built that way is noise. This harness instead launches once and flips each
effect LIVE with the radiance_config console command, so every measurement shares one baseline.

    python tools/effectaudit.py                        all scenes
    python tools/effectaudit.py --scene farm-day       one scene

For each scene: measure the full-config frame time, then for each effect toggle it off (god rays
ship off, so those are toggled ON instead), wait out a full FrameCost window, read the frame time
from radiance_report, and put the toggle back. The effect's cost is the difference. The baseline
is re-measured every few effects; if it moved more than the drift budget the rows between are
marked suspect instead of silently kept.

Output: docs/local/perf/effectaudit-<date>/ - raw reports plus a ranked summary table.
"""
import json, os, re, shutil, subprocess, sys, time

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from perfbench import (rpc, kill_game, wait_bridge, load_save, GAME, CONFIG, REPORT,
                       FRAME_LINE, COST_LINE, SCENES)

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "docs", "local", "perf", "effectaudit-" + time.strftime("%Y-%m-%d"))

# Frames roll into FrameCost's 300-frame window; uncapped that fills in under a second, so most
# of this wait is letting the scene's own caches (bakes, masks, mirrors) resettle after a toggle.
SETTLE = 6

# (label, config key). Every boolean is flipped AWAY from whatever the player's config holds, so
# an effect they run with off (fog here, god rays elsewhere) is measured by switching it ON, and
# the cost column always means the same thing: how much the frame grows when the effect runs.
TOGGLES = [
    ("bloom",             "BloomEnabled"),
    ("color grade",       "ColorGradeEnabled"),
    ("god rays",          "GodRaysEnabled"),
    ("fog",               "FogEnabled"),
    ("cloud shadows",     "CloudShadowEnabled"),
    ("tilt shift",        "TiltShiftEnabled"),
    ("vignette",          "VignetteEnabled"),
    ("chromatic ab.",     "ChromaticAberrationEnabled"),
    ("water ripple",      "WaterEnabled"),
    ("water reflection",  "WaterReflection"),
    ("lighting",          "LightingEnabled"),
    ("flood GI",          "FloodLightingEnabled"),
    ("light shadows",     "LightingShadows"),
    ("dir. shadows",      "DirectionalShadowsEnabled"),
    ("object shadows",    "DirectionalShadowObjects"),
    ("shadow blur",       "DirectionalShadowBlur"),      # float: measured at 0 vs as-configured
    ("window effects",    "WindowEffectsEnabled"),
]

# NOTHING ELSE MAY RUN ON THE MACHINE DURING AN AUDIT. Frame time is a whole-machine
# measurement: a compiler, a browser agent, an indexer - any of them shows up as the baseline
# jumping 3 ms to 25 ms and every row after it turning to noise. This happened; it was our own
# tooling both times. Run the audit alone, or read garbage.

# Re-measure the baseline this often. Between two baselines the machine is assumed still; if
# they disagree by more than the budget, every row between them is printed with a mark.
#
# Set to 1 (baseline before every toggle, cost measured against the MEAN of the baselines on
# either side) after the first full run showed the farm scene drifting enough to hand vignette a
# negative half-millisecond cost, which is not a thing. Interleaving doubles the run time and is
# the difference between a table and a rumor.
BASELINE_EVERY = 1
DRIFT_BUDGET_MS = 0.15


# Frame times that are a refresh rate in costume, not a measurement. The uncap does NOT stick:
# one run re-capped at 180 Hz mid-scene and another fell all the way back to 60, so the request
# is re-sent before every single measurement and the reading is rejected if it still lands on a
# cap. Rejecting matters as much as re-sending - a capped baseline under real readings handed
# fog a +2 ms cost it does not have.
CAPS_MS = (16.67, 8.33, 6.94, 5.55, 4.17)


def looks_capped(ms):
    return any(abs(ms - c) < 0.06 for c in CAPS_MS)


def fresh_report(tag):
    """Run radiance_report, wait for the file write, return (frame_ms, text).

    A reading that sits on a known cap is retried after re-asserting uncapped and letting a
    fresh 300-frame window fill; only if it STILL reads capped is it returned (a machine can
    legitimately idle near a number, but three windows in a row on the exact cap is the cap).
    """
    for attempt in range(3):
        rpc("set", {"uncapped": True}, timeout=60)
        if attempt > 0:
            time.sleep(4)
        was = os.path.getmtime(REPORT) if os.path.exists(REPORT) else 0
        rpc("console", {"command": "radiance_report"}, timeout=120)
        for _ in range(40):
            if os.path.exists(REPORT) and os.path.getmtime(REPORT) > was:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError("radiance_report never wrote")
        time.sleep(0.5)
        text = open(REPORT, encoding="utf-8", errors="replace").read()
        shutil.copy2(REPORT, os.path.join(OUT, tag + ".txt"))
        m = FRAME_LINE.search(text)
        if not m:
            raise RuntimeError("report has no frame-time block (no frames measured?)")
        ms = float(m.group(1))
        if not looks_capped(ms):
            return ms, text
        print(f"    [{tag}: {ms:.2f} ms sits on a frame cap - re-asserting uncapped]", flush=True)
    return ms, text


def set_cfg(key, value):
    rpc("console", {"command": f"radiance_config {key} {value}"}, timeout=60)


def main():
    only = None
    if "--scene" in sys.argv:
        only = sys.argv[sys.argv.index("--scene") + 1]
    os.makedirs(OUT, exist_ok=True)
    base_cfg = json.load(open(CONFIG, encoding="utf-8"))

    kill_game()
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    rows = []          # (scene, label, cost_ms, baseline_ms, suspect, measured_by_on)
    try:
        if not wait_bridge():
            print("bridge never came up")
            return
        load_save()
        rpc("set", {"pauseInactive": False, "uncapped": True}, timeout=60)

        for scene, loc, x, y, tod in SCENES:
            if only and scene != only:
                continue
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            rpc("set", {"time": tod, "uncapped": True}, timeout=60)
            rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
            time.sleep(SETTLE)

            base, _ = fresh_report(f"{scene}-baseline-0")
            print(f"\n{scene}: baseline {base:.3f} ms ({1000/base:.0f} fps)", flush=True)
            pending = []   # rows since the last good baseline
            since = 0

            for label, key in TOGGLES:
                original = base_cfg.get(key)
                if original is None:
                    print(f"  {label:18s} SKIP - {key} not in config.json", flush=True)
                    continue
                if isinstance(original, bool):
                    flipped, flipped_is_on = str(not original).lower(), not original
                else:
                    if not original:   # float already 0: nothing to flip against
                        print(f"  {label:18s} SKIP - already 0", flush=True)
                        continue
                    flipped, flipped_is_on = "0", False
                # Pin the clock back before every measurement. Six real seconds of settle is ten
                # game minutes, so a full audit walked the game past 2am mid-run: the farmer
                # collapsed, the game warped him home, and the last scene measured a pass-out
                # cutscene at 24 ms a frame instead of a shop interior at two.
                rpc("set", {"time": tod}, timeout=60)
                set_cfg(key, flipped)
                time.sleep(SETTLE)
                try:
                    ms, _ = fresh_report(f"{scene}-{key}")
                    # Positive always means "this effect makes frames longer by this much",
                    # whichever direction the flip went.
                    cost = (ms - base) if flipped_is_on else (base - ms)
                    marker = " (measured by turning ON)" if flipped_is_on else ""
                    pending.append([scene, label, cost, base, False, flipped_is_on])
                    print(f"  {label:18s} {cost:+.3f} ms{marker}", flush=True)
                except Exception as e:
                    print(f"  {label:18s} FAILED {e}", flush=True)
                finally:
                    set_cfg(key, str(original))
                since += 1
                if since >= BASELINE_EVERY:
                    since = 0
                    rpc("set", {"time": tod}, timeout=60)
                    time.sleep(SETTLE)
                    nb, _ = fresh_report(f"{scene}-baseline-recheck")
                    if abs(nb - base) > DRIFT_BUDGET_MS:
                        for r in pending:
                            r[4] = True
                        print(f"  [baseline moved {base:.3f} -> {nb:.3f}; rows above marked suspect]", flush=True)
                    # Score pending rows against the mean of the flanking baselines, so a
                    # steady drift between the two lands in the middle instead of all on one
                    # side. (With BASELINE_EVERY=1 there is exactly one pending row.) The
                    # re-anchor direction depends on which way the flip went: an off-flip's
                    # cost is baseline-minus-off, an on-flip's is on-minus-baseline.
                    mid = (base + nb) / 2
                    for r in pending:
                        r[2] += (mid - r[3]) if not r[5] else (r[3] - mid)
                        r[3] = mid
                    rows.extend(pending)
                    pending = []
                    base = nb
            rows.extend(pending)

        lines = [f"# Per-effect cost, measured live in one run - {time.strftime('%Y-%m-%d %H:%M')}",
                 "", "Cost = how much longer the frame is with the effect on, uncapped, same run,",
                 "same baseline. Rows marked ~ had the baseline move under them; re-run those.",
                 "", "| scene | effect | cost ms | of baseline | |", "|---|---|---|---|---|"]
        for scene, label, cost, base, suspect, _on in sorted(rows, key=lambda r: -r[2]):
            lines.append(f"| {scene} | {label} | {cost:+.3f} | {cost/base*100:+.1f}% | {'~' if suspect else ''} |")
        summary = os.path.join(OUT, "summary.md")
        open(summary, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        print("\n" + "\n".join(lines))
        print(f"\nwritten to {summary}")
    finally:
        kill_game()


if __name__ == "__main__":
    main()
