"""Prove a screenshot warp lands somewhere a player can actually stand.

The description page is a list of "warp here at this time and take the picture", and a warp
that buries the camera inside a house is worse than no warp at all: the game's debug warp
does not check the tile, it just moves the player, and a tile under a building leaves them
stuck in the dark wondering whether the mod broke.

So every spot on that list is warped to here first, and answered with the mod's own
radiance_tile, which prints the tile the player is REALLY on plus every map layer that has
art there. Two things disqualify a spot:

  * the player did not end up on the tile that was asked for (the game moved them), and
  * a Buildings-layer tile sits under them, which outdoors means a wall or a roof.

    python tools/shotspots.py                 check every spot
    python tools/shotspots.py --spot beach-sea

Results land in docs/local/description/shotspots-<date>.txt, one block per spot, and a
verdict table at the end.
"""
import argparse, json, os, re, subprocess, sys, time, urllib.error, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
SMAPI_LOG = os.path.join(os.environ["APPDATA"], "StardewValley", "ErrorLogs", "SMAPI-latest.txt")
OUT = os.path.join(REPO, "docs", "local", "description",
                   "shotspots-" + time.strftime("%Y-%m-%d") + ".txt")

# name, location, x, y, time, weather, what the shot is for.
# Outdoor spots are the ones that can go wrong; the indoor ones are listed too so the whole
# shot list is proven by one run rather than half of it.
SPOTS = [
    ("beach-sea",      "Beach",    30, 30, 900,  "sun",   "sea reflection, morning"),
    ("beach-rain",     "Beach",    30, 30, 1200, "rain",  "rain, rings on the water"),
    ("beach-dusk",     "Beach",    30, 30, 1830, "sun",   "golden hour over water"),
    ("town-square",    "Town",     45, 55, 700,  "sun",   "long morning shadows"),
    ("town-night",     "Town",     45, 55, 2200, "sun",   "lamps, flood GI"),
    ("town-storm",     "Town",     45, 55, 2000, "storm", "lightning, bolt, wet street"),
    ("town-snow",      "Town",     45, 55, 1400, "snow",  "snowfall"),
    ("town-bridge",    "Town",     95, 13, 1200, "rain",  "the bridge edge, water is water"),
    ("mountain-lake",  "Mountain", 46,  5, 1800, "sun",   "lake and the mine bridge"),
    ("forest-pond",    "Forest",   60, 50, 1900, "sun",   "fireflies over the pond"),
    ("forest-day",     "Forest",   40, 20, 800,  "sun",   "god rays through the trees"),
    ("farm-front",     "Farm",     64, 15, 1700, "sun",   "blossoms, farm at golden hour"),
    ("busstop",        "BusStop",  20, 25, 1000, "sun",   "open road, cloud shadows"),
    ("railroad",       "Railroad", 30, 40, 1500, "sun",   "wide open, tilt shift"),
    ("saloon",         "Saloon",   15, 20, 2000, "sun",   "indoor lamps, window glow"),
    ("seedshop",       "SeedShop",  5, 18, 800,  "sun",   "indoor morning light"),
    # Alternates, kept in the list so a spot that fails has a proven replacement rather than
    # another guess. Costed nothing to check while the game was already up.
    ("alt-town-a",     "Town",     30, 60, 1200, "sun",   "alternate town camera"),
    ("alt-town-b",     "Town",     50, 60, 1200, "sun",   "alternate town camera"),
    ("alt-town-c",     "Town",     45, 65, 1200, "sun",   "alternate town camera"),
    ("alt-beach",      "Beach",    40, 25, 1200, "sun",   "alternate beach camera"),
    ("alt-mountain",   "Mountain", 55, 20, 1200, "sun",   "alternate lake camera"),
    ("alt-forest",     "Forest",   55, 55, 1200, "sun",   "alternate pond camera"),
]

TILE_HEAD = re.compile(r"=== Tile \((\d+),(\d+)\) in (\S+) ===")
LAYER_LINE = re.compile(r"^(?:\[[^\]]*\]\s*)?\s*([A-Za-z0-9_-]+): sheet=", re.M)
# The neighbourhood grid marks the tile the player is on with brackets, and its legend is the
# question this whole tool asks: . ground, D deck, W water, # wall, ^ roof, o void.
CENTRE_TILE = re.compile(r"\[([.DWG#^ow])\]")
STANDABLE = set(".D")


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
            time.sleep(2)
        except Exception as e:
            last = str(e)
            time.sleep(2)
    raise RuntimeError(f"{tool}: gave up after {tries} tries ({last[:200]})")


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
    try:
        return os.path.getsize(SMAPI_LOG)
    except OSError:
        return 0


def log_since(mark):
    with open(SMAPI_LOG, "r", encoding="utf-8", errors="replace") as f:
        f.seek(mark)
        return f.read()


def read_tile():
    """One radiance_tile, and the part of the log it wrote."""
    mark = log_size()
    rpc("console", {"command": "radiance_tile"}, timeout=120)
    time.sleep(1.5)
    text = log_since(mark)
    head = TILE_HEAD.search(text)
    if not head:
        return None, None, text
    return (int(head.group(1)), int(head.group(2))), head.group(3), text


def check(spot):
    name, loc, x, y, tod, weather, purpose = spot
    # Warped twice on purpose: setting the clock can move the player (the game decides to
    # wake them or pass them out), so the second warp is what the reading is taken from.
    rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
    rpc("set", {"time": tod}, timeout=60)
    rpc("goto", {"location": loc, "x": x, "y": y}, timeout=120)
    rpc("console", {"command": "radiance_weather " + weather}, timeout=60)

    # A warp lands on the NEXT tick and a console command runs on THIS one, so the first
    # reading after a warp describes where the player used to be. The first run of this tool
    # reported every spot one behind for exactly that reason. Read until the tile settles.
    actual = where = text = None
    for _ in range(6):
        actual, where, text = read_tile()
        if actual == (x, y) and where and where.startswith(loc):
            break
        time.sleep(2)

    if actual is None:
        return {"name": name, "verdict": "NO READING", "text": (text or "")[-800:]}
    layers = set(LAYER_LINE.findall(text))
    centre = CENTRE_TILE.search(text)
    surface = centre.group(1) if centre else "?"
    verdict = "ok"
    if actual != (x, y) or not (where or "").startswith(loc):
        verdict = f"NEVER ARRIVED, sat at {where} {actual[0]},{actual[1]}"
    elif surface == "?":
        verdict = "no surface reading"
    elif surface not in STANDABLE:
        verdict = f"UNSTANDABLE, surface '{surface}'"
    return {"name": name, "loc": loc, "asked": [x, y], "actual": list(actual), "where": where,
            "surface": surface, "layers": sorted(layers), "verdict": verdict,
            "purpose": purpose, "time": tod, "weather": weather, "text": text}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spot", action="append", help="only these names")
    args = ap.parse_args()
    spots = [s for s in SPOTS if not args.spot or s[0] in args.spot]

    if not wait_bridge(deadline=20):
        print("bridge is down, launching the game")
        # The game's own console output is swallowed: inherited, it lands in this tool's log
        # in UTF-16 and buries the only lines worth reading.
        subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if not wait_bridge():
            raise SystemExit("the bridge never came up")
    load_save()
    rpc("set", {"pauseInactive": False}, timeout=30)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    rows = []
    with open(OUT, "w", encoding="utf-8") as f:
        for spot in spots:
            print(f"-- {spot[0]}")
            row = check(spot)
            rows.append(row)
            f.write(f"===== {row['name']} =====\n")
            f.write(json.dumps({k: v for k, v in row.items() if k != "text"},
                               ensure_ascii=False, indent=2) + "\n")
            f.write((row.get("text") or "")[-3000:] + "\n\n")
            f.flush()
        f.write("\n===== verdicts =====\n")
        for row in rows:
            f.write(f"{row['name']:<16} {row['verdict']}\n")

    print("\nname             verdict")
    for row in rows:
        print(f"{row['name']:<16} {row['verdict']}")
    print("\nwritten to", OUT)


if __name__ == "__main__":
    main()
