"""Dump every labelling profile in turn, then merge the results into one maps.json.

A location only reaches the dump if its mod is loaded, and some map mods cannot be loaded at
the same time, so full coverage needs several passes. Tilesheet ART needs no pass at all:
radiance_mapdump all reads every PNG off disk, Mods (disabled) included.

    python multidump.py [profile ...]      (default: Label-Wide-1..3)
"""
import json, os, shutil, subprocess, sys, time, urllib.error, urllib.request
sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
PY = r"D:\Program\anaconda3\python.exe"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")


def rpc(tool, args=None, timeout=1800, tries=60):
    """One bridge call, retried while the game's MAIN THREAD is still busy.

    The bridge answers `ping` from its listener thread, so "bridge is up" says nothing about
    the game being able to RUN anything: with ~100 mods the main thread stays busy loading for
    a minute and every queued job returns 500 "main-thread job timed out". drive.py treats that
    as fatal, which is what made all three passes fail while the profiles themselves were fine.
    """
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
            raise RuntimeError(f"{tool}: HTTP {e.code} {last[:300]}")
    raise RuntimeError(f"{tool}: main thread never freed up ({last[:200]})")


def wait_bridge(deadline=420):
    t0 = time.time()
    while time.time() - t0 < deadline:
        try:
            rpc("ping", timeout=5, tries=1)
            return True
        except Exception:
            time.sleep(3)
    return False


def load_and_dump():
    """Load the first save if needed, then dump every map and sheet."""
    st = rpc("state", timeout=30).get("result", {})
    if not st.get("ready"):
        saves = rpc("load").get("result", {}).get("saves", [])
        if not saves:
            raise RuntimeError("no saves found")
        rpc("load", {"save": saves[0]})
        t0 = time.time()
        while time.time() - t0 < 900:
            st = rpc("state", timeout=30).get("result", {})
            if st.get("ready"):
                break
            time.sleep(5)
        if not st.get("ready"):
            raise RuntimeError("save never finished loading")
    try:
        rpc("set", {"pauseInactive": False}, timeout=30)
    except Exception:
        pass          # not fatal: it only stops the game pausing while unfocused
    return rpc("dump", {"all": True})


def kill_game():
    subprocess.run(["taskkill", "/F", "/IM", "StardewModdingAPI.exe"],
                   capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "Stardew Valley.exe"], capture_output=True)
    time.sleep(4)


def bridge_down(deadline=60):
    """Wait until the old bridge really is gone, so drive.py cannot talk to a dying game."""
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


def check_log():
    """Mods SMAPI refused to load this run. SMAPI writes SMAPI-latest.player-2.txt when a
    second instance is up, so always take the NEWEST file rather than the obvious name."""
    d = os.path.expanduser(r"~\AppData\Roaming\StardewValley\ErrorLogs")
    try:
        logs = [os.path.join(d, f) for f in os.listdir(d)
                if f.startswith("SMAPI-latest") and f.endswith(".txt")]
        newest = max(logs, key=os.path.getmtime)
        txt = open(newest, encoding="utf-8", errors="replace").read()
    except Exception:
        return []
    bad = []
    for line in txt.splitlines():
        if "ERROR SMAPI" in line and ("skipped" in line.lower()
                                      or "aren't installed" in line
                                      or "installed incorrectly" in line):
            bad.append(line.strip()[:160])
        elif line.strip().startswith("- ") and "because it requires mods" in line:
            bad.append(line.strip()[:160])
    return bad[:12]


def snapshot(name):
    """Save whatever is enabled right now, so the run can be undone."""
    enabled = {}
    base = os.path.join(GAME, "Mods")
    for cat in sorted(os.listdir(base)):
        cp = os.path.join(base, cat)
        if not os.path.isdir(cp):
            continue
        mods = sorted(m for m in os.listdir(cp) if os.path.isdir(os.path.join(cp, m)))
        if mods:
            enabled[cat] = mods
    doc = {"name": name, "created": time.strftime("%Y-%m-%d"),
           "note": "Automatic snapshot taken before the multi-profile label dump run.",
           "enabled": enabled}
    json.dump(doc, open(os.path.join(GAME, "mod-profiles", f"{name}.json"), "w",
                        encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"snapshot saved as profile '{name}' ({sum(len(v) for v in enabled.values())} mods)")


def run_profile(prof):
    print(f"\n{'='*70}\n{prof}\n{'='*70}", flush=True)
    kill_game()
    bridge_down()
    r = subprocess.run([PY, os.path.join(HERE, "applyprofile.py"), prof],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(r.stdout.strip() or r.stderr.strip(), flush=True)
    if r.returncode != 0:
        print(f"  profile switch FAILED, skipping {prof}")
        return None
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    t0 = time.time()
    if not wait_bridge():
        print(f"  bridge never came up for {prof}")
        return None
    try:
        load_and_dump()
    except Exception as e:
        print(f"  dump FAILED for {prof}: {e}")
        return None
    # Read the log before trusting the dump. A pack that refused to install still lets the
    # game start and the dump succeed - it just quietly contributes no maps, which looks
    # identical to a good pass until someone counts locations. Ridgeside did exactly that.
    for line in reversed(check_log()):
        print(f"  ! {line}", flush=True)

    src = os.path.join(HFDIR, "maps.json")
    dst = os.path.join(HFDIR, f"maps-{prof}.json")
    shutil.copy2(src, dst)
    mb = os.path.getsize(dst) / 1e6
    d = json.load(open(dst, encoding="utf-8"))
    # Count artPng too. Sheet art moved out of the JSON into one PNG per sheet beside it, so
    # reading only the legacy inline `art` printed "0 sheets" for every pass of a healthy run,
    # which reads as the dump collecting no images at all.
    print(f"  -> {os.path.basename(dst)}  {mb:.0f} MB  "
          f"{len(d['locations'])} locations  {len(d.get('art', {})) + len(d.get('artPng', {}))} sheets  "
          f"({time.time()-t0:.0f}s)", flush=True)
    return dst


def merge(paths, out):
    """Union of every pass, keeping the ALTERNATIVES rather than the first one seen.

    The rotating profiles swap mods that replace the same location - three farmhouse packs all
    own "FarmHouse" - so a plain first-wins union would silently drop two thirds of the point
    of running three passes. When a later pass carries a DIFFERENT map under a name already
    taken, it is kept under "<name> [profile]" so every variant is there to label.
    """
    # artPng (one PNG per sheet beside maps.json) as well as the legacy inline `art`. Dropping
    # artPng here would produce a merged dump with no sheet art at all, which looks like the
    # tool losing every image rather than the merge losing a key.
    locations, art, artPng, artSrc, water, groups, sigs = {}, {}, {}, {}, {}, [], set()
    season, variants = None, 0
    for p in paths:
        tag = os.path.basename(p).replace("maps-", "").replace(".json", "")
        d = json.load(open(p, encoding="utf-8"))
        season = season or d.get("season")
        for k, v in d["locations"].items():
            if k not in locations:
                locations[k] = v
                continue
            if locations[k].get("layers") == v.get("layers"):
                continue                    # same map, nothing to keep
            alt = f"{k} [{tag}]"
            if alt not in locations:
                locations[alt] = v
                variants += 1
        for k, v in d.get("art", {}).items():
            art.setdefault(k, v)
        for k, v in d.get("artPng", {}).items():
            artPng.setdefault(k, v)
        for k, v in d.get("artSrc", {}).items():
            artSrc.setdefault(k, v)
        for k, v in d.get("water", {}).items():
            water[k] = sorted(set(water.get(k, [])) | set(v))
        for g in d.get("animGroups", []):
            s = "|".join(g)
            if s not in sigs:
                sigs.add(s)
                groups.append(g)
        del d
        print(f"  merged {os.path.basename(p)}: running total "
              f"{len(locations)} locations / {len(art)+len(artPng)} sheets", flush=True)
    doc = {"format": "hf-mapdump-v1", "season": season, "locations": locations,
           "art": art, "artPng": artPng, "artSrc": artSrc, "water": water,
           "animGroups": groups}
    json.dump(doc, open(out, "w", encoding="utf-8"), ensure_ascii=False)
    print(f"\nwrote {out}  {os.path.getsize(out)/1e6:.0f} MB  "
          f"{len(locations)} locations ({variants} kept as profile variants)  "
          f"{len(art)+len(artPng)} sheets  {len(groups)} anim groups")


def all_pass_files():
    """Every pass ever dumped, oldest first, not just the ones this run produced.

    Merging only THIS run's output overwrites maps.json with a subset. Resuming a half-finished
    coverage run - passes 18 to 34 after 1 to 17 - would have replaced 2,324 merged locations
    with the ~1,600 from the second half, and it would have looked like a successful run. The
    per-pass files are the durable artefact; maps.json is derived and always rebuilt from all
    of them.
    """
    out = []
    for f in sorted(os.listdir(HFDIR)):
        if not (f.startswith("maps-") and f.endswith(".json")):
            continue
        if f.startswith(("maps-spring", "maps-summer", "maps-fall", "maps-winter")):
            continue           # seasonal exports, a different axis
        out.append(os.path.join(HFDIR, f))
    return out


def restore_profile():
    """Put the machine back the way it was found. multidump used to leave whatever profile the
    last pass applied, so the game was unplayable until someone remembered to switch back."""
    snap = os.path.join(GAME, "mod-profiles", "_before-label-run.json")
    if not os.path.exists(snap):
        print("no _before-label-run snapshot to restore")
        return
    r = subprocess.run([PY, os.path.join(HERE, "applyprofile.py"), "_before-label-run"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    print("restored profile _before-label-run" if r.returncode == 0
          else f"RESTORE FAILED: {(r.stderr or r.stdout)[:300]}")


if __name__ == "__main__":
    profiles = sys.argv[1:] or ["Label-Wide-1", "Label-Wide-2", "Label-Wide-3"]
    if profiles == ["--mappasses"]:
        import json as _j
        profiles = _j.load(open(os.path.join(HERE, "mappasses.json"), encoding="utf-8"))
    if profiles == ["--merge-only"]:
        # Rebuild maps.json from what is already on disk, dumping nothing. The way back from a
        # run that died half way.
        merge(all_pass_files(), os.path.join(HFDIR, "maps.json"))
        sys.exit(0)
    if not os.path.exists(os.path.join(GAME, "mod-profiles", "_before-label-run.json")):
        snapshot("_before-label-run")     # never overwrite a real snapshot with a run's leftovers
    done = []
    try:
        for x in profiles:
            got = run_profile(x)
            if got:
                done.append(got)
    finally:
        # ALWAYS, even when a pass raises: a game left running holds the bridge port, and the
        # next launch then talks to the wrong instance. That is how two windows ended up open.
        kill_game()
        restore_profile()
    if not done:
        sys.exit("no pass produced a dump")
    merge(all_pass_files(), os.path.join(HFDIR, "maps.json"))
