"""Photograph the mod at its best, everywhere worth photographing, at 1080p.

    python tools/shotgallery.py                     every scene
    python tools/shotgallery.py --list              print the plan, run nothing
    python tools/shotgallery.py --scene beach-aurora
    python tools/shotgallery.py --save YURU         which save to load (default YURU)
    python tools/shotgallery.py --scenes-only       prove the spots, take no pictures

This is the gallery pass: everything switched on and turned up, one frame per place, chosen for
how it looks rather than for what it proves. gallery.local.py's on/off pass answers "what does
this effect do"; a store page answers "is this worth installing", and that is a different set of
pictures.

Three rules it will not break, because each one has already ruined a shot:

  * THE PLAYER NEVER STANDS IN WATER OR INSIDE A BUILDING. radiance_tile prints a neighbourhood
    grid whose legend is exactly this question (. ground, D deck, W water, # wall, ^ roof,
    o void) with the tile under the player in brackets. Anything but ground or deck and the
    scene is SKIPPED and reported, rather than shot and quietly wrong. The game also slides a
    warp that lands somewhere illegal without saying so, so where the player ACTUALLY ended up
    is what gets checked, never where they were sent. Every scene carries alternates, because a
    tile proven on one save proves nothing on another: Farm 64,15 is where the whole
    verification harness stands, and in the YURU save it is a wall.
  * The frame is frozen before it is taken, so nothing is caught mid-animation.
  * Config is written with radiance_config, which is memory-only. The player's config.json is
    never opened, let alone moved aside. (A wrapper that moved a config aside and was killed
    before it put it back cost an hour on 2026-08-28.)

Output: ~/Documents/Radiance-Shots-1.7.0/<scene>.png, plus report.md naming every spot that was
refused, every setting that is NOT the out-of-box default, and every effect a still cannot show.
"""
import argparse, gzip, json, os, re, shutil, sys, subprocess, time, urllib.error, urllib.request

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PORT_FILE = os.path.join(GAME, "Mods", "00_Frameworks", "SDV-AgentBridge", "port.txt")
SMAPI_LOG = os.path.join(os.environ["APPDATA"], "StardewValley", "ErrorLogs", "SMAPI-latest.txt")
DUMPS = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Dumps")
OUT = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Shots-1.7.0")
TMP_DUMP = "shotgallery-tmp"

# The bridge's own screenshot comes back at the WINDOW size whatever the zoom is (measured:
# 1280x720 at zoom 0.75 and at 0.667) and it carries the HUD. The mod's dump carries no HUD and
# comes out at window/zoom, so the zoom is what sets the resolution: 1280/0.6667 is 1920 and
# 720/0.6667 is 1080. Every PNG is measured after it is written, and anything that is not WANT
# is reported rather than filed.
WANT = (1920, 1080)
ZOOM = 1280.0 / 1920.0

TILE_HEAD = re.compile(r"=== Tile \((\d+),(\d+)\) in (\S+) ===")
CENTRE_TILE = re.compile(r"\[([.DWG#^ow])\]")
STANDABLE = set(".D")

# What the gallery is shot with. The first block is the 1.7.0 default look; the second is the
# things that ship OFF and are turned on here because the page is a shop window. Both are listed
# in report.md, because a gallery that quietly shows settings nobody gets out of the box is how
# a store page ends up promising what the download does not do.
GALLERY_DEFAULTS = {
    "Enabled": True, "WaterEnabled": True, "WaterReflection": True,
    "FloodLightingEnabled": True, "FloodGiModel": "Cascades",
    "LightingEnabled": True, "LightingShadows": True,
    "DirectionalShadowsEnabled": True, "DirectionalShadowObjects": True,
    "LightShadowSilhouettes": True, "LightShadowProps": True,
    "BloomEnabled": True, "CloudShadowEnabled": True, "TiltShiftEnabled": True,
    "FogEnabled": True, "WindowEffectsEnabled": True, "WindowBeamEnabled": True,
    "FoliageSwayEnabled": True, "AuroraEnabled": True, "ShootingStarsEnabled": True,
    "HeatHazeEnabled": True, "ParticlesEnabled": True, "PrecipitationEnabled": True,
    "WetWorldEnabled": True,
}
GALLERY_EXTRAS = {
    # setting: (value, what it is, what it ships at)
    "SpriteReliefEnabled": (True, "sprite relief", "off"),
    "SheetUpscaleEnabled": (True, "sprites at twice the texels", "off"),
    "FloodColourBleed": (0.4, "bounced light takes the colour it bounced off", "0"),
    "GodRaysEnabled": (True, "lamp shafts", "off"),
    "GoldenHourStrength": (0.6, "golden hour", "0"),
}

# `alts` is tried in order when the first tile turns out to be a wall, a roof or water.
FARM_ALTS = [(64, 17), (63, 18), (68, 18), (60, 20), (55, 22), (70, 22), (48, 26), (40, 30)]
HOUSE_ALTS = [(10, 10), (8, 8), (7, 11), (11, 7)]
TOWN_ALTS = [(45, 55), (30, 60), (50, 60), (45, 65), (54, 68)]
BEACH_ALTS = [(30, 30), (40, 25), (25, 30), (35, 28)]

SCENES = [
    # --- water and sky ------------------------------------------------------------------
    dict(name="beach-aurora", loc="Beach", x=30, y=30, alts=BEACH_ALTS, hour=2200,
         season="winter", weather="sun", setup=["radiance_aurora on"],
         teardown=["radiance_aurora auto"],
         about="aurora curtains in the sea, the 1.7.0 headline"),
    # 18:30 in summer is still flat daylight and 06:30 is already up: the first pair of these
    # came out as two midday shots that were hard to tell apart, and neither showed the golden
    # hour the caption promised. Sunset moved late, and dawn moved to fall, where the sun rises
    # an hour later than it does in summer, so six in the morning is actually first light.
    dict(name="beach-sunset", loc="Beach", x=30, y=30, alts=BEACH_ALTS, hour=1930,
         season="summer", weather="sun", about="golden hour over the sea"),
    dict(name="beach-dawn", loc="Beach", x=30, y=30, alts=BEACH_ALTS, hour=620,
         season="fall", weather="sun", about="first light, long shadows down the sand"),
    dict(name="beach-rain", loc="Beach", x=30, y=30, alts=BEACH_ALTS, hour=1400,
         season="summer", weather="rain", about="rain rings on the sea, wet sand"),
    # 75,25 put the lake behind the player and 19:00 in fall is past the light: the caption said
    # reflections and the picture had none. 60,20 stands on the shore with the water and the tree
    # on the far bank both in frame, at the hour the low sun is still on them.
    dict(name="mountain-lake", loc="Mountain", x=60, y=20, alts=[(66, 22), (68, 24), (57, 25)],
         hour=1730, season="fall", weather="sun", about="the lake at dusk, reflections"),
    # The fall the gallery kept missing is not on Mountain at all: SVE moved it to the summit, and
    # every mountain candidate came back without one in frame. 50,26 is asked rather than the tile
    # it lands on, because the game slides this one and 50,26 is the ask that was proven to land
    # where the fall runs from the top of the frame down into the pool, with the boat and the dock
    # in the corner. Alternates are the other three spots that had it in frame.
    dict(name="mountain-waterfall", loc="Custom_AdventurerSummit", x=50, y=26,
         alts=[(48, 34), (56, 28), (52, 30)],
         hour=1500, season="summer", weather="sun",
         about="the fall, its mist and the settled pool"),
    dict(name="town-bridge", loc="Town", x=95, y=13, alts=[(94, 13), (96, 13), (95, 14)],
         hour=1200, season="summer", weather="rain", about="the bridge, rain on the river"),
    dict(expect_light=True, rings=True, name="forest-pond-night", loc="Forest", x=60, y=50, alts=[(58, 50), (62, 50), (60, 48)],
         hour=2100, season="summer", weather="sun", about="fireflies over the pond"),

    # --- lamps after dark ---------------------------------------------------------------
    # 54,68 is the pet pen behind the houses: no lamp, no lit window, nothing for a night shot to
    # be about. 34,57 stands on the square in front of the shop, where the warm windows and the
    # street lamps both land on the cobbles.
    dict(expect_light=True, name="town-night", loc="Town", x=34, y=57,
         alts=[(43, 60), (30, 90), (30, 60)], hour=2200,
         season="summer", weather="sun",
         about="street lamps, cascades GI, relief and rim light all at once"),
    dict(expect_light=True, name="town-storm", loc="Town", x=45, y=55, alts=TOWN_ALTS, hour=2000,
         season="summer", weather="storm", about="lightning, wet street, lamps"),
    dict(expect_light=True, rings=True, name="farm-night", loc="Farm", x=64, y=17, alts=FARM_ALTS, hour=2200,
         season="summer", weather="sun",
         about="the farm after dark, lamp shadows off the props"),
    dict(expect_light=True, name="saloon", loc="Saloon", x=15, y=20, alts=[(14, 20), (16, 20), (15, 21)],
         hour=2000, season="summer", weather="sun", about="a room full of lamps"),
    dict(expect_light=True, name="farmhouse-night", loc="FarmHouse", x=9, y=9, alts=HOUSE_ALTS, hour=2000,
         season="summer", weather="sun", about="the hearth, the window, the indoor curve"),
    # Six, the hour the player wakes, because that is when the room is at its darkest AND the sun
    # is at its lowest: the mod's own morning dim holds through 06:00 and lifts over the next two
    # hours, so a shot at half seven has a brighter room and a higher sun, which is the wrong half
    # of both. What a window throws only reads against a dim floor.
    # Morning indoors, which is the one time of day the window pass has anything to say: the lit
    # pane, the beam through it and the patch of sun it lays on the floor, with the furniture
    # standing in it. The clock is written BEFORE the warp as well as after, because the game
    # only refreshes a room's window glows when the location is ENTERED - winding it forward
    # while already inside leaves every window at its night state and the beams never appear.
    dict(name="farmhouse-morning", loc="FarmHouse", x=9, y=9, alts=HOUSE_ALTS, hour=600,
         season="summer", weather="sun",
         about="morning through the bedroom window: the beam, the sun on the floor, the shadows it throws"),

    # --- daylight ------------------------------------------------------------------------
    dict(name="town-morning", loc="Town", x=45, y=55, alts=TOWN_ALTS, hour=700,
         season="spring", weather="sun", about="long morning shadows down the square"),
    # Standing north of the fountain put it against the bottom edge and cut it in half. Five tiles
    # south of that the whole basin, both lamps and the hedges are inside the frame.
    dict(name="town-fountain-dusk", loc="Town", x=26, y=22, alts=[(27, 20), (26, 21), (29, 19)],
         hour=1830, season="fall", weather="sun", about="the fountain in fall, low sun"),
    dict(name="town-snow", loc="Town", x=45, y=55, alts=TOWN_ALTS, hour=1400,
         season="winter", weather="snow", about="snowfall over the square"),
    dict(name="farm-golden", loc="Farm", x=64, y=17, alts=FARM_ALTS, hour=700,
         season="summer", weather="sun", about="crops at golden hour"),
    dict(name="forest-canopy", loc="Forest", x=40, y=20, alts=[(42, 20), (38, 22), (45, 25)],
         hour=1000, season="summer", weather="sun",
         about="sun through the canopy, dapple on the path"),
    dict(name="forest-fall", loc="Forest", x=40, y=20, alts=[(42, 20), (38, 22), (45, 25)],
         hour=1600, season="fall", weather="sun", about="the same wood in fall colour"),
    dict(name="railroad", loc="Railroad", x=30, y=40, alts=[(32, 40), (28, 40), (30, 42)],
         hour=1500, season="summer", weather="sun",
         about="wide open, tilt shift at its most obvious"),
    dict(name="busstop", loc="BusStop", x=20, y=25, alts=[(22, 25), (18, 25), (20, 23)],
         hour=1000, season="spring", weather="sun", about="cloud shadows crossing the road"),
    dict(name="seedshop-morning", loc="SeedShop", x=5, y=18, alts=[(6, 18), (5, 17), (7, 19)],
         hour=800, season="spring", weather="sun", about="morning light through a shop window"),
    # Pierre's storefront from OUTSIDE, which is where the window reflection actually shows: the
    # player standing in the glass. Deliberately in flat daylight with nothing carried - a light
    # source near the player blows the pane out and the reflection is the first thing to go, so
    # this is the one scene where the absence of a lamp IS the shot. The ring is off for the whole
    # run now (see start()), so this scene only has to deal with the pane itself: it turns off how
    # much of a nearby lamp's glow is painted into the glass, and turns the reflection up to meet
    # a bright spring noon.
    dict(name="pierre-glass", loc="Town", x=43, y=57,
         alts=[(44, 58), (45, 58), (43, 58), (46, 57), (42, 57), (44, 59)],
         hour=1100, season="spring", weather="sun",
         config={"WindowLightGlowStrength": 0, "WindowReflectionStrength": 1.8},
         about="the player reflected in Pierre's shop windows, daylight, no lamp glow on the panes"),

    # --- heat and depth --------------------------------------------------------------------
    dict(name="caldera", loc="Caldera", x=22, y=17, alts=[(21, 16), (23, 18), (20, 17)],
         hour=1200, season="summer", weather="sun", about="lava, its heat haze and its sparks"),
    dict(name="bathhouse", loc="BathHouse_Pool", x=10, y=6, alts=[(9, 6), (11, 6), (10, 7)],
         hour=1200, season="summer", weather="sun", about="steam over the hot spring"),

    # --- second pass: the places with the strongest light of their own --------------------
    # These are guesses at coordinates rather than tiles anyone has stood on, which is what the
    # alternates are for. A location the save has never unlocked answers "ended up in X, not Y"
    # and is refused, which is the right outcome: a locked island cannot be photographed.
    dict(expect_light=True, name="mines-torchlit", loc="UndergroundMine20", x=10, y=10,
         alts=[(12, 12), (8, 8), (14, 10), (10, 14), (16, 16), (20, 12)],
         hour=1200, season="summer", weather="sun",
         about="wall torches in the dark, which is where lamp shadows have the most to say"),
    dict(expect_light=True, name="mines-lava", loc="UndergroundMine100", x=10, y=10,
         alts=[(12, 12), (8, 8), (14, 10), (10, 14), (16, 16), (20, 12)],
         hour=1200, season="summer", weather="sun", about="the lava levels, heat and torchlight"),
    dict(name="skull-cavern", loc="SkullCave", x=10, y=10,
         alts=[(8, 8), (12, 12), (6, 6), (14, 8), (10, 6)],
         hour=1200, season="summer", weather="sun", about="the cavern mouth"),
    dict(name="volcano", loc="VolcanoDungeon0", x=20, y=30,
         alts=[(22, 32), (18, 28), (24, 30), (16, 34), (26, 26)],
         hour=1200, season="summer", weather="sun", about="the volcano's own lava light"),
    dict(expect_light=True, name="sewer", loc="Sewer", x=16, y=10, alts=[(18, 12), (14, 10), (20, 14), (12, 8)],
         hour=1200, season="summer", weather="sun", about="lamplight underground"),
    dict(name="witch-swamp", loc="WitchSwamp", x=25, y=25,
         alts=[(22, 28), (28, 22), (20, 24), (30, 28), (24, 20)],
         hour=1200, season="summer", weather="sun", about="the swamp, its own green dark"),
    dict(name="wizard-tower", loc="WizardHouse", x=10, y=12,
         alts=[(8, 12), (12, 12), (10, 14), (6, 10)],
         hour=1400, season="summer", weather="sun", about="a room lit by something other than fire"),
    dict(name="greenhouse", loc="Greenhouse", x=10, y=12,
         alts=[(8, 12), (12, 14), (10, 16), (6, 10), (14, 12)],
         hour=1000, season="summer", weather="sun", about="light through a glass roof onto crops"),
    dict(name="desert", loc="Desert", x=30, y=40, alts=[(28, 42), (32, 38), (26, 44), (34, 40)],
         hour=1200, season="summer", weather="sun", about="flat bright sand, the opposite palette"),
    dict(name="secret-woods", loc="Woods", x=25, y=20, alts=[(22, 22), (28, 18), (20, 24), (30, 22)],
         hour=1000, season="summer", weather="sun", about="deep canopy, dapple and damp"),
    dict(name="island-south", loc="IslandSouth", x=15, y=25,
         alts=[(12, 28), (18, 22), (20, 30), (10, 24), (22, 26)],
         hour=1500, season="summer", weather="sun", about="the island beach and its water"),
    dict(name="island-north", loc="IslandNorth", x=30, y=30,
         alts=[(28, 32), (32, 28), (26, 34), (34, 30)],
         hour=1700, season="summer", weather="sun", about="the volcano seen from the island"),
    dict(name="community-centre", loc="CommunityCenter", x=32, y=12,
         alts=[(30, 14), (34, 10), (28, 12), (36, 14)],
         hour=1200, season="summer", weather="sun", about="the hall, and whatever state it is in"),
    dict(name="backwoods", loc="Backwoods", x=20, y=20, alts=[(18, 22), (22, 18), (16, 24), (24, 20)],
         hour=1700, season="fall", weather="sun", about="the path north in low autumn sun"),

    # --- weather and hours the first pass never showed --------------------------------------
    # 54,68 rather than 45,55: the same square at 45,55 came back with nothing lit in the frame,
    # and 54,68 is where town-night found its lamps.
    dict(expect_light=True, name="town-rain-night", loc="Town", x=54, y=68, alts=TOWN_ALTS, hour=2200,
         season="fall", weather="rain", about="rain at night: wet street, lamps, rings in the puddles"),
    dict(name="town-dawn-fog", loc="Town", x=45, y=55, alts=TOWN_ALTS, hour=600,
         season="fall", weather="sun", about="six in the morning, when the fog has the square"),
    dict(expect_light=True, name="farm-winter-night", loc="Farm", x=64, y=17, alts=FARM_ALTS, hour=2200,
         season="winter", weather="snow", about="snow on the ground, lamps over it"),
    dict(name="forest-green-rain", loc="Forest", x=40, y=20, alts=[(42, 20), (38, 22), (45, 25)],
         hour=1200, season="summer", weather="greenrain",
         about="green rain, the strangest light the game has"),
    dict(expect_light=True, name="railroad-night", loc="Railroad", x=30, y=40, alts=[(32, 40), (28, 40), (30, 42)],
         hour=2200, season="summer", weather="sun", about="the spa's own light on an empty platform"),
    dict(expect_light=True, rings=True, name="beach-night", loc="Beach", x=30, y=30, alts=BEACH_ALTS, hour=2200,
         season="summer", weather="sun", about="the pier after dark, without an aurora to carry it"),
]

# Things a still frame cannot show, and why. Not shot: two identical-looking stills filed as an
# effect's evidence are worse than an honest gap.
GIF_ONLY = [
    ("foliage sway", "the whole effect is motion. One frame is a tree tilted a fraction of a "
                     "degree, which is indistinguishable from a tree."),
    ("shooting stars", "under a second long, at a spot picked at random, and only over water. "
                       "A still would have to be lucky rather than representative."),
    ("aurora surge", "the curtains photograph well and beach-aurora has them; the surge running "
                     "along one and dying is the part that makes people look, and it is motion."),
    ("leaf shimmer", "plus or minus five percent of brightness travelling through a canopy. Two "
                     "stills of it look like the same still."),
    ("waterfall mist and hot-spring steam", "they photograph as speckle. What reads as mist is "
                                            "the drift. mountain-waterfall and bathhouse show "
                                            "where they are; a clip shows what they do."),
    ("water ripple and the mirror's breakup", "temporal by construction."),
    ("cascades against flood while walking", "not a picture at all. The win is the worst single "
                                             "frame during a walk (2.33 ms against 0.26), which "
                                             "is a number, not a look."),
]


# ---- bridge ---------------------------------------------------------------

def rpc(tool, args=None, timeout=180, tries=40):
    port = int(open(PORT_FILE).read().strip())
    body = json.dumps({"tool": tool, "args": args or {}}).encode()
    last = ""
    for _ in range(tries):
        try:
            req = urllib.request.Request(f"http://127.0.0.1:{port}/rpc", data=body,
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            # The bridge answers 500 with "main-thread job timed out" while a save is loading,
            # and every tool here that forgot to retry it died on its first run.
            last = e.read().decode("utf-8", errors="replace")
            time.sleep(4)
        except Exception as e:
            last = str(e)
            time.sleep(2)
    raise RuntimeError(f"{tool}: gave up ({last[:200]})")


def kill_game():
    for image in ("StardewModdingAPI.exe", "Stardew Valley.exe"):
        subprocess.run(["taskkill", "/F", "/IM", image], capture_output=True)
    time.sleep(4)
    # A port file left by a killed run makes the next one answer ping and then refuse everything
    # after it. Cost a whole capture on 2026-08-28.
    try:
        os.remove(PORT_FILE)
    except OSError:
        pass


def start(save_hint):
    kill_game()
    subprocess.Popen([os.path.join(GAME, "StardewModdingAPI.exe")], cwd=GAME)
    deadline = time.time() + 420
    while time.time() < deadline:
        try:
            rpc("ping", timeout=5, tries=1)
            break
        except Exception:
            time.sleep(3)
    else:
        raise SystemExit("bridge never came up")
    if not rpc("state", timeout=30).get("result", {}).get("ready"):
        saves = rpc("load").get("result", {}).get("saves", [])
        if not saves:
            raise SystemExit("no saves found")
        wanted = [s for s in saves if save_hint.lower() in str(s).lower()]
        if not wanted:
            raise SystemExit("no save matching %r in %s" % (save_hint, saves))
        print("loading save %s" % wanted[0], flush=True)
        rpc("load", {"save": wanted[0]})
        deadline = time.time() + 900
        while time.time() < deadline:
            if rpc("state", timeout=30).get("result", {}).get("ready"):
                break
            time.sleep(5)
        else:
            raise SystemExit("save never finished loading")
    rpc("set", {"pauseInactive": False}, timeout=30)
    rpc("set", {"zoom": ZOOM}, timeout=60)
    # The mines, the skull cavern and the volcano have monsters, and the window where they can
    # reach the player is real: the warp lands, then radiance_tile is asked and answered, and
    # only after that does the frame freeze. Four seconds is plenty to be killed in, and on
    # 2026-08-28 it was - the 1.7.0 shot list said "monsters: debug invincible first" and this
    # tool had not carried that across. A death does not touch the save, because nothing here
    # ever saves, but it ends the run and leaves a rescue dialogue over everything after it.
    clear_the_way()
    make_invincible()


def clear_the_way(rounds=8):
    """Get past whatever the save opens with, and prove it is past.

    A save does not necessarily hand you a farmer standing in a field. It can open inside a
    cutscene, on a letter, on a dialogue box, or on the morning message, and every one of those
    swallows the warp that comes next: the tool then photographs the farmhouse it never left and
    reports the spot it was asked for. That is not a hypothetical, it is what the Shima save does.

    `clear` takes the menus and boxes; `debug EndEvent` skips a running event and warns harmlessly
    when there is none. Both are repeated, because an event can hand straight over to a letter.

    The proof is a WARP, not the absence of a complaint: we ask to be moved somewhere and read back
    where the game says we are. Anything else believes a machine that was not listening.
    """
    for attempt in range(rounds):
        rpc("clear", timeout=30)
        rpc("console", {"command": "debug EndEvent"}, timeout=60)
        time.sleep(2)
        rpc("goto", {"location": "Farm", "x": 64, "y": 17}, timeout=150)
        wait_until_settled(30)
        where = rpc("state", timeout=30).get("result", {}).get("location")
        if where == "Farm":
            if attempt:
                print("the way was blocked; cleared after %d rounds" % (attempt + 1), flush=True)
            return True
        print("still stuck in %s after %d round(s)" % (where, attempt + 1), flush=True)
    raise SystemExit("could not get out of %s: the save opens with something this cannot skip"
                     % rpc("state", timeout=30).get("result", {}).get("location"))


def make_invincible():
    """Invincible, and proven so, on whichever build happens to be deployed.

    radiance_invincible SETS it and says which it ended up as. The game's own debug invincible
    TOGGLES, so any script that ends up calling it twice quietly hands the player back to the
    monsters; that is how the author died in the mines on 2026-08-28.

    An older build has no radiance_invincible, and a run against one is exactly when this matters
    (the mine case walks into monsters either way), so the log is read back rather than assumed:
    if the command was not recognised, the game's own toggle is used instead. Calling both would
    turn it straight back off.
    """
    mark = log_mark()
    rpc("console", {"command": "radiance_invincible on"}, timeout=60)
    time.sleep(2)
    answer = log_since(mark)
    if "invincible = True" in answer:
        print("invincible: on (radiance_invincible)", flush=True)
        return
    print("invincible: this build has no radiance_invincible, using the game's toggle", flush=True)
    rpc("console", {"command": "debug invincible"}, timeout=60)
    # Which scenes keep the glow ring on is decided per scene by wears_ring(); it is set inside
    # arrive(), while the clock is still running. Nothing is saved either way and the ring comes
    # back on the next load.


def cfg(key, value):
    if isinstance(value, bool):
        value = "true" if value else "false"
    rpc("console", {"command": "radiance_config %s %s" % (key, value)}, timeout=60)


def apply_gallery_look():
    for key, value in GALLERY_DEFAULTS.items():
        cfg(key, value)
    for key, (value, _what, _ships) in GALLERY_EXTRAS.items():
        cfg(key, value)


def log_mark():
    try:
        return os.path.getsize(SMAPI_LOG)
    except OSError:
        return 0


def log_since(mark):
    with open(SMAPI_LOG, "r", encoding="utf-8", errors="replace") as fh:
        fh.seek(mark)
        return fh.read()


# ---- the rule that matters -------------------------------------------------

def standable(loc, x, y):
    """Warp there and answer whether a player could really be standing on that tile."""
    # A dialogue box left open swallows the warp and every command after it. classic-ab and the
    # label sweep both clear before moving for this reason.
    rpc("clear", timeout=30)
    rpc("goto", {"location": loc, "x": x, "y": y}, timeout=150)
    time.sleep(2)
    state = rpc("state", timeout=30).get("result", {})
    if state.get("location") != loc:
        return False, "ended up in %r, not %r" % (state.get("location"), loc)
    mark = log_mark()
    rpc("console", {"command": "radiance_tile"}, timeout=90)
    time.sleep(2)
    text = log_since(mark)
    head = TILE_HEAD.search(text)
    if not head:
        return False, "radiance_tile printed nothing"
    got = (int(head.group(1)), int(head.group(2)))
    centre = CENTRE_TILE.search(text)
    if not centre:
        return False, "stood on %s, but the neighbourhood grid did not print" % (got,)
    kind = centre.group(1)
    names = {".": "ground", "D": "deck", "W": "WATER", "#": "WALL", "^": "ROOF", "o": "void",
             "G": "grass", "w": "shallow water"}
    if kind not in STANDABLE:
        return False, "tile %s is %s - refusing to photograph it" % (got, names.get(kind, kind))
    if got != (x, y):
        return True, "%s %s (the game moved the player; asked %d,%d)" % (
            got, names.get(kind, kind), x, y)
    return True, "%s %s" % (got, names.get(kind, kind))


# ---- capture ---------------------------------------------------------------

def wait_until_settled(seconds=60):
    """Wait for the game to STOP FADING rather than sleeping a guess. A warp into a new map fades
    through black, and how long that takes depends on the map: eight seconds covered the town and
    the beach and did not cover the desert, the island, the greenhouse, the secret woods, the
    backwoods, the community centre or the volcano - nine frames of pure black were filed as
    gallery shots on 2026-08-28 because nothing here was watching. The bridge has carried a
    `fading` flag all along."""
    settled = 0
    deadline = time.time() + seconds
    while time.time() < deadline:
        if rpc("state", timeout=30).get("result", {}).get("fading"):
            settled = 0
        else:
            settled += 1
            if settled >= 3:
                return
        time.sleep(2)


def step_outside_first(loc):
    """Leave wherever we are and come back, so the target location is ENTERED with the clock
    already set.

    The game refreshes a location's window glows only on entry. Writing the clock before the warp
    is not enough on its own, because when the previous scene was in the SAME location the warp
    never crosses a boundary and nothing is refreshed: farmhouse-morning follows farmhouse-night,
    so the room was still wearing its 20:00 windows at six in the morning and the beam the shot is
    about was missing. The same holds for the run of Town scenes. One extra fade per scene buys
    every scene an honest room.
    """
    away = ("Town", 45, 55) if loc == "Farm" else ("Farm", 64, 17)
    rpc("clear", timeout=30)
    rpc("goto", {"location": away[0], "x": away[1], "y": away[2]}, timeout=150)
    wait_until_settled(45)


def wears_ring(scene):
    """Whether the player keeps the glow ring on for this scene.

    The save wears one, so the player carried a lamp into every shot, and the question each scene
    has to answer is whether that lamp is the SUBJECT or a competitor.

    It is on after dark and under any weather but sun, off in clear daylight.

    The case for taking it off at night was that a pool of the player's own light sits on top of
    the street lamps and the hearth those shots exist to show. The case for keeping it, which is
    the one that wins, is that the light the ring throws is drawn by this mod: per-light shadows
    from a lamp the player is carrying is the 1.7.0 headline, and it is the only one of the
    lighting features that the viewer can picture themselves standing in. Nobody walks the valley
    at ten at night with nothing in their hands, so a night shot without it is not the neutral
    version, it is a version of the game nobody plays.

    Flat CLEAR daylight is the one place it is only in the way: there is nothing for a lamp to add
    at noon, and the pale wash around the player reads as a bug in the mod rather than as a ring in
    the picture. Sunset at 19:30 counts as daylight so the golden hour is shot clean, and the mines
    and the volcano keep it off because those shots are about what the wall torches do.

    `rings=` on a scene overrides all of it.
    """
    if "rings" in scene:
        return scene["rings"]
    return scene["weather"] != "sun" or scene["hour"] < 600 or scene["hour"] >= 2000


def arrive(loc, x, y, hour, season, weather, alts=None, rings=False):
    rpc("console", {"command": "radiance_freeze off"}, timeout=60)
    # Set while the clock is running and long before the freeze: done inside a scene's own setup
    # it runs after the frame is already frozen and the light does not change.
    rpc("console", {"command": "radiance_rings " + ("on" if rings else "off")}, timeout=60)
    rpc("set", {"season": season}, timeout=90)
    rpc("set", {"time": hour}, timeout=60)
    step_outside_first(loc)
    ok, detail = standable(loc, x, y)
    tried = 1
    for ax, ay in alts or []:
        if ok:
            break
        ok, detail = standable(loc, ax, ay)
        tried += 1
    if not ok:
        return False, "every tile refused (%d tried); last: %s" % (tried, detail)
    if tried > 1:
        detail = "%s (after %d refused)" % (detail, tried - 1)
    rpc("console", {"command": "radiance_weather " + weather}, timeout=60)
    # Indoors the game only refreshes a room's window glows when the location is ENTERED, so the
    # clock is written before the warp as well as after it.
    rpc("set", {"time": hour}, timeout=60)
    # Wait for the game to STOP FADING rather than sleeping a guess. A warp into a new map fades
    # through black, and how long that takes depends on the map: eight seconds covered the town
    # and the beach and did not cover the desert, the island, the greenhouse, the secret woods,
    # the backwoods, the community centre or the volcano - nine frames of pure black were filed
    # as gallery shots on 2026-08-28 because nothing here was watching. The bridge has carried a
    # `fading` flag all along.
    settled = 0
    deadline = time.time() + 60
    while time.time() < deadline:
        if rpc("state", timeout=30).get("result", {}).get("fading"):
            settled = 0
        else:
            settled += 1
            if settled >= 3:
                break
        time.sleep(2)
    time.sleep(6)
    rpc("console", {"command": "radiance_freeze on"}, timeout=60)
    time.sleep(3)
    return True, detail


def dump_png(path_png):
    target = os.path.join(DUMPS, TMP_DUMP)
    shutil.rmtree(target, ignore_errors=True)
    rpc("console", {"command": "radiance_dump " + TMP_DUMP}, timeout=120)
    meta_path = os.path.join(target, "metadata.json")
    for _ in range(40):
        if os.path.exists(meta_path):
            time.sleep(2)
            break
        time.sleep(1)
    else:
        return None, "the dump never appeared"
    with open(meta_path, encoding="utf-8") as fh:
        meta = json.load(fh)
    entry = meta.get("buffers", {}).get("frame_out")
    if entry is None:
        return None, "the dump had no frame_out"
    with gzip.open(os.path.join(target, entry["file"]), "rb") as fh:
        raw = np.frombuffer(fh.read(), dtype=np.uint8)
    h, w, bpp = entry["height"], entry["width"], entry["bytesPerPixel"]
    if raw.size != h * w * bpp:
        return None, "the dump is %d bytes, not %dx%dx%d" % (raw.size, h, w, bpp)
    rgb = raw.reshape(h, w, bpp)[:, :, :3]
    # MonoGame's Color is RGBA and Bgra32 is not. Getting this backwards does not fail, it turns
    # a lake orange, which reads as a mod bug rather than as a tool bug.
    if entry.get("format") in ("Bgra32", "Bgr32"):
        rgb = rgb[:, :, ::-1]
    # A blank frame filed as a picture is worse than a missing one: it looks like a result. Even
    # the mine at midnight is not this dark, so anything under a couple of values of mean
    # brightness is the fade, not the scene.
    brightness = float(rgb.mean())
    if brightness < 2.0:
        shutil.rmtree(target, ignore_errors=True)
        return None, "the frame came out black (mean %.2f/255) - the fade had not finished" % brightness
    os.makedirs(os.path.dirname(path_png) or ".", exist_ok=True)
    Image.fromarray(np.ascontiguousarray(rgb)).save(path_png)
    shutil.rmtree(target, ignore_errors=True)
    return (w, h, float(np.percentile(rgb, 99))), None


def set_ring(worn):
    """Put the ring on or take it off, and let the light actually change.

    The lighting only moves while the render clock is running, so this unfreezes, switches, waits,
    and freezes again. Doing it inside a scene's setup, after the freeze, left the light exactly
    where it was and the shot came out wearing whatever the previous scene had.
    """
    rpc("console", {"command": "radiance_freeze off"}, timeout=60)
    rpc("console", {"command": "radiance_rings " + ("on" if worn else "off")}, timeout=60)
    time.sleep(4)
    rpc("console", {"command": "radiance_freeze on"}, timeout=60)
    time.sleep(3)


def capture(scene, results, path_png, label):
    """One frame, measured and filed, or a reason it was not."""
    name = scene["name"]
    size, error = dump_png(path_png)
    if error:
        if os.path.exists(path_png):
            os.remove(path_png)
        results.append((name + label, "FAILED", error, None))
        print("  %s%s" % (label + " " if label else "", error), flush=True)
        return
    width, height, highlight = size
    # A night scene whose brightest pixels never get past a quarter of the range has no light
    # SOURCE in the frame, which is a different fault from being dark: town-rain-night came back
    # with a 99th percentile of 49 while every other night shot sat between 167 and 240, and it
    # was filed anyway because nothing was looking. Dark is the point; unlit is a bad camera.
    #
    # In a paired run the unlit frame is the POINT of the pair, so it is kept and reported rather
    # than deleted: the whole question being asked is what the scene looks like with the player's
    # lamp taken away.
    unlit = scene.get("expect_light") and highlight < 80
    if unlit and not label:
        if os.path.exists(path_png):
            os.remove(path_png)
        why = ("nothing in frame is lit (brightest pixels reach %d/255; a lamp reads 170+). "
               "The camera is somewhere with no light source." % highlight)
        results.append((name, "FAILED", why, None))
        print("  %s" % why, flush=True)
        return
    verdict = "OK" if (width, height) == WANT else "WRONG SIZE %dx%d" % (width, height)
    if unlit:
        verdict += " (nothing in frame is lit)"
    print("  %s%dx%d  highlight %d" % (label + " " if label else "", width, height, highlight),
          flush=True)
    results.append((name + label, verdict, "", (width, height)))


def take(scene, results, pairs=False):
    name = scene["name"]
    print("\n=== %s: %s ===" % (name, scene["about"]), flush=True)
    ok, detail = arrive(scene["loc"], scene["x"], scene["y"], scene["hour"],
                        scene["season"], scene["weather"], scene.get("alts"),
                        True if pairs else wears_ring(scene))
    print("  spot: %s" % detail, flush=True)
    if not ok:
        results.append((name, "REFUSED", detail, None))
        return
    for command in scene.get("setup", []):
        rpc("console", {"command": command}, timeout=60)
    apply_gallery_look()
    for key, value in scene.get("config", {}).items():
        cfg(key, value)
    time.sleep(5)
    if not pairs:
        capture(scene, results, os.path.join(OUT, name + ".png"), "")
        for command in scene.get("teardown", []):
            rpc("console", {"command": command}, timeout=60)
        results[-1] = (results[-1][0], results[-1][1], detail, results[-1][3])
        return
    # Both answers to the ring question, from the SAME spot and the same clock, so the choice is
    # made by looking rather than by argument. The warp and the fade are what cost the time here,
    # and neither is paid twice.
    capture(scene, results, os.path.join(OUT, "ring", name + ".png"), " [ring]")
    set_ring(False)
    capture(scene, results, os.path.join(OUT, "noring", name + ".png"), " [noring]")
    set_ring(True)
    for command in scene.get("teardown", []):
        rpc("console", {"command": command}, timeout=60)


def write_report(results, save_name):
    about = {s["name"]: s["about"] for s in SCENES}
    lines = ["# 1.7.0 gallery, %s" % time.strftime("%Y-%m-%d %H:%M"), "",
             "Save: %s. %dx%d, no HUD (the mod's own frame dump at zoom %.4f)."
             % (save_name, WANT[0], WANT[1], ZOOM), "",
             "Every spot was warped to and then asked, through radiance_tile, what the player is",
             "really standing on. Anything but ground or deck is refused rather than",
             "photographed, and the alternates are tried in order.", "",
             "| scene | verdict | what it is | spot |", "|---|---|---|---|"]
    for name, verdict, detail, _size in results:
        lines.append("| %s | %s | %s | %s |" % (name, verdict, about.get(name, ""), detail))
    lines += ["", "## Not the out-of-box look", "",
              "These ship OFF or at zero and are turned ON for the gallery. Anything on the store",
              "page that shows them has to say so, or the page promises what the download does",
              "not do.", ""]
    for key, (value, what, ships) in GALLERY_EXTRAS.items():
        lines.append("- **%s** (`%s`) shot at `%s`, ships at `%s`" % (what, key, value, ships))
    worn = sorted(s["name"] for s in SCENES if wears_ring(s))
    bare = sorted(s["name"] for s in SCENES if not wears_ring(s))
    lines += ["",
              "The save wears a glow ring, so the player was carrying a lamp into every shot. "
              "It stays on after dark and under any weather but sun, because the light it throws "
              "is drawn by this mod and a lamp in the player's hand is the one lighting feature "
              "the viewer can picture themselves standing in. It comes off in flat clear "
              "daylight, where there is nothing for a lamp to add and the pale wash around the "
              "player reads as a bug rather than as a ring.",
              "",
              "- ring ON (%d): %s" % (len(worn), ", ".join(worn)),
              "- ring OFF (%d): %s" % (len(bare), ", ".join(bare))]
    overrides = [(s["name"], s["config"]) for s in SCENES if s.get("config")]
    if overrides:
        lines += ["", "And these scenes override a setting of their own:", ""]
        for name, config in overrides:
            pairs = ", ".join("`%s` = `%s`" % (k, v) for k, v in config.items())
            lines.append("- **%s**: %s" % (name, pairs))
    if os.path.isdir(os.path.join(OUT, "ring")):
        lines += ["", "This run was taken in PAIRS: `ring/` and `noring/` each hold every scene, "
                      "shot from the same spot at the same clock with the ring on and off, so the "
                      "choice is made by looking. The split above is only what a single run would "
                      "have used."]
    lines += ["", "## Not photographed, because a still cannot show it", ""]
    for what, why in GIF_ONLY:
        lines.append("- **%s** - %s" % (what, why))
    lines += ["", "These want a short clip rather than a frame."]
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, "report.md")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scene", action="append", choices=[s["name"] for s in SCENES])
    ap.add_argument("--save", default="YURU")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--scenes-only", action="store_true")
    # Take EVERY scene twice from the same spot, ring on and ring off, into ring/ and
    # noring/. Which scenes want the player carrying a lamp is a question about the look,
    # and it was going round in circles being argued instead of looked at.
    ap.add_argument("--pairs", action="store_true")
    args = ap.parse_args()
    wanted = [s for s in SCENES if not args.scene or s["name"] in args.scene]

    if args.list:
        for s in wanted:
            print("%-22s %s %d,%d %04d %-6s %-5s - %s"
                  % (s["name"], s["loc"], s["x"], s["y"], s["hour"], s["season"],
                     s["weather"], s["about"]))
        print("\nturned on for the gallery but shipped off:")
        for key, (value, what, ships) in GALLERY_EXTRAS.items():
            print("  %s (%s) at %s, ships at %s" % (what, key, value, ships))
        print("\nnot photographed (needs a clip):")
        for what, why in GIF_ONLY:
            print("  %s: %s" % (what, why))
        return 0

    start(args.save)
    results = []
    try:
        for scene in wanted:
            if args.scenes_only:
                ok, detail = arrive(scene["loc"], scene["x"], scene["y"], scene["hour"],
                                    scene["season"], scene["weather"], scene.get("alts"))
                print("%-22s %s %s" % (scene["name"], "ok " if ok else "REFUSED", detail),
                      flush=True)
                results.append((scene["name"], "OK" if ok else "REFUSED", detail, None))
            else:
                take(scene, results, args.pairs)
    finally:
        kill_game()
    path = write_report(results, args.save)
    print("\nwritten to %s" % path, flush=True)
    for name, verdict, detail, _ in results:
        if verdict != "OK":
            print("  %s: %s - %s" % (verdict, name, detail), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
