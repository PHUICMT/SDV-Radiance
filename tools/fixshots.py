"""Photograph what a fix changed, from both sides of it.

    python tools/fixshots.py --tag after            with the current build deployed
    python tools/fixshots.py --tag before           with a pre-fix build deployed
    python tools/fixshots.py --tag after --case fountain-relief

Each case is a place, a clock and a set of switches chosen so the thing a commit changed is the
thing in the middle of the frame. The same list is shot twice, once against each build, into
`~/Documents/Radiance-Fixshots/<tag>/`, and `tools/fixreport.py` puts the two side by side.

A picture of "after" on its own proves nothing: it is a picture of a scene. The pair is the
evidence, and the pair is only evidence if the spot, the clock, the season, the weather and every
setting are identical, which is why this reuses shotgallery's arrive() rather than its own warp.

Some cases carry a `console` command as well: its output is captured from the SMAPI log next to
the PNG, because "the crab has a shadow" is a claim a still can support and "we decided to cast
for it" is one only the diagnostic can.

Cases a still frame cannot carry are listed in NOT_SHOT rather than faked.
"""
import argparse, os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shotgallery as gallery

OUT = os.path.join(os.path.expanduser("~"), "Documents", "Radiance-Fixshots")

# Everything relief-related has to be shot with relief ON, because it ships off and the fix is
# invisible without it. The gallery look is applied to every case first; these are on top.
RELIEF_ON = {"SpriteReliefEnabled": True, "SheetUpscaleEnabled": True}

CASES = [
    dict(name="oasis-palms", loc="Desert", x=26, y=41,
         alts=[(26, 43), (24, 41), (28, 42), (25, 40), (30, 40)],
         hour=1200, season="summer", weather="sun",
         commit="27af864 + 5f4eb33",
         look="The palms standing in the oasis pond. Before: the water is painted over their "
              "trunks and they end at the waterline. After: the trunk runs down to its base and "
              "the reflection stamps the trunk piece as well as the canopy."),
    dict(name="fountain-relief", loc="Town", x=26, y=22,
         alts=[(27, 20), (26, 21), (29, 19)],
         hour=1830, season="fall", weather="sun", config=RELIEF_ON,
         commit="dd44c7a + 338d053",
         look="The fountain's animated water tiles. Before: a dark bevelled edge boxes each tile, "
              "because a translated map tilesheet was not recognised as the map's own art. After: "
              "the tiles are flat like every other map tile. This one only ever showed in a game "
              "running in Thai, Chinese, Japanese or Russian."),
    dict(name="tree-trunk-seam", loc="Forest", x=40, y=20,
         alts=[(42, 20), (38, 22), (45, 25)],
         hour=1000, season="summer", weather="sun", config=RELIEF_ON,
         commit="9e98041 + e068735",
         look="Any full-grown tree. Before: a dark line runs across the trunk where the canopy "
              "sprite's art stops and the separate trunk sprite takes over, because relief shaded "
              "each of the two as if that row were the edge of a real object. After: the trunk "
              "carries the canopy's shading through the join.",
         console="radiance_reliefdraws"),
    dict(name="mine-floor-badge", loc="UndergroundMine20", x=10, y=10,
         alts=[(12, 12), (8, 8), (14, 10), (10, 14), (16, 16), (20, 12)],
         hour=1200, season="summer", weather="sun",
         commit="a8bbe29",
         look="Before: the mine's floor number is painted across the captured frame, because the "
              "game draws it over everything and the dump takes what is on the screen. After: the "
              "badge is suppressed for the one draw the dump captures and nothing else changes."),
    dict(name="wild-animal-shadow", loc="Beach", x=30, y=30,
         alts=[(40, 25), (25, 30), (35, 28)],
         hour=600, season="summer", weather="sun", ab_shadows_off=True,
         commit="88aaa2f",
         look="A crab or a duck from SH's Wild Animals. Before: nothing under it, while every "
              "other creature on the beach casts. After: it casts like anything else, and exactly "
              "once. The pair also answers whether we still swallow the blob the mod paints for "
              "itself: the BEFORE frame is what a suppressed blob and a skipped cast look like "
              "together, so if the suppression had stopped working there would be a blob in the "
              "before column, and if it were only half working there would be two shadows in the "
              "after one. The creatures wander, so the frame is evidence only if one is IN it: "
              "check the radiance_shadows output beside this file, which names every caster.",
         console="radiance_shadows all"),
    dict(name="creature-shadow-forest", loc="Forest", x=60, y=25,
         alts=[(58, 25), (62, 26), (55, 28), (40, 20)],
         hour=600, season="summer", weather="sun", ab_shadows_off=True,
         commit="88aaa2f",
         look="The same fix, in the wood, where a different set of the pack's creatures walks.",
         console="radiance_shadows all"),
    # On Shima rather than YURU, because YURU has no stable and the horse is the whole point. Read
    # out of the save file rather than guessed: Shima's stable sits at 49,7 and its pet bowl at
    # 53,7, and a stable puts its horse at its own tile plus one, so standing at 50,11 holds both
    # animals in the upper half of a 30-by-17 frame. Nothing here builds a stable into anyone's
    # farm to make the picture happen.
    dict(name="pet-and-horse-shadow", save="Shima", loc="Farm", x=50, y=11,
         alts=[(52, 11), (48, 12), (51, 10), (53, 11), (46, 10)],
         hour=600, season="summer", weather="sun", ab_shadows_off=True,
         commit="067f935 + 4194723",
         look="The horse by its stable and the cat or dog by its bowl, in low morning sun where a "
              "shadow is longest. Before: a horse and a pet are NPCs, so they took a PERSON's "
              "ground foreshortening and their shadow stood up on edge beside them. After: they "
              "lie across the ground like a farm animal, which is the shape they are. A horse "
              "facing left also had its shadow drawn facing right, because the game mirrors one "
              "set of frames rather than holding two and the silhouette was cut from the frame "
              "without the mirror.",
         console="radiance_shadows all"),
    # Same farm, three tiles south of the row of lightning rods: a tall thin thing on open ground
    # is where an anchor error is impossible to miss.
    dict(name="object-shadow-foot", save="Shima", loc="Farm", x=54, y=13,
         alts=[(53, 13), (55, 13), (54, 14), (52, 13)],
         hour=600, season="summer", weather="sun",
         config={"CloudShadowEnabled": False},
         commit="88dbdf7 + dc7320c",
         look="The row of lightning rods. Before: a strip of lit ground sat between each rod's "
              "copper base and the start of its own shadow, because the shadow was anchored 20px "
              "above the tile's bottom edge and then pivoted on its CELL rather than on the row "
              "its art ends on. After: the shadow starts at the base. The cloud shadow is turned "
              "off for this pair: it drifts, it darkens the whole ground, and it ruined four "
              "comparisons of this before anyone noticed."),
]

NOT_SHOT = [
    ("the tree's reflection leaning with the tree", "42aced5",
     "the whole change is that the mirror follows a motion. Two stills of it are two stills of a "
     "tree at slightly different angles."),
    ("the reflection surviving a tree being chopped", "e389c29",
     "it needs an axe swing at the moment the tree starts to fall. Nothing here can hold a frame "
     "at that instant on both builds."),
    ("a ridden horse casting again", "4194723",
     "the player has to be ON the horse, and nothing in the console mounts one: the game has no "
     "debug command for it and the bridge has none either. The fix is in the same commit as the "
     "mirrored shadow, which IS shot."),
    ("the trunk tipping with its canopy", "777ed46",
     "the seam it closes only opens while the wind is tilting the tree, and how far it is tilted "
     "at the moment of capture is not something the harness sets."),
]


def capture_console(command, path_txt):
    """Run a diagnostic and keep what it printed, next to the picture."""
    mark = gallery.log_mark()
    gallery.rpc("console", {"command": command}, timeout=120)
    time.sleep(3)
    text = gallery.log_since(mark)
    with open(path_txt, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    return sum(1 for line in text.splitlines() if line.strip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True, choices=["before", "after"])
    ap.add_argument("--case", action="append", choices=[c["name"] for c in CASES])
    ap.add_argument("--save", default="YURU")
    args = ap.parse_args()
    wanted = [c for c in CASES if not args.case or c["name"] in args.case]
    folder = os.path.join(OUT, args.tag)
    os.makedirs(folder, exist_ok=True)

    # A case may name a save of its own: the horse lives on one farm and not another, and a
    # different save means a different launch of the game. Grouped so each save is loaded once, in
    # the order the cases are listed.
    order, groups = [], {}
    for case in wanted:
        save = case.get("save", args.save)
        if save not in groups:
            order.append(save)
            groups[save] = []
        groups[save].append(case)

    done, failed = [], []
    for save in order:
        print("\n########## save: %s ##########" % save, flush=True)
        run_group(save, groups[save], folder, done, failed)

    print("\n%s: %d shot, %d refused" % (args.tag, len(done), len(failed)), flush=True)
    for name, why in failed:
        print("  REFUSED %s: %s" % (name, why), flush=True)
    with open(os.path.join(folder, "spots.txt"), "w", encoding="utf-8", newline="\n") as fh:
        for name, detail in done:
            fh.write("%s\t%s\n" % (name, detail))
    return 0


def set_our_shadows(on):
    """Turn this mod's shadows on or off and let the change reach the screen.

    The lighting only moves while the render clock runs, so the freeze has to come off first. A
    switch flipped under a frozen frame changes nothing and looks exactly like a switch that does
    not work, which is how pierre-glass spent a week ignoring a ring command in its own setup.
    """
    gallery.rpc("console", {"command": "radiance_freeze off"}, timeout=60)
    gallery.cfg("DirectionalShadowsEnabled", on)
    time.sleep(4)
    gallery.rpc("console", {"command": "radiance_freeze on"}, timeout=60)
    time.sleep(3)


def run_group(save, cases, folder, done, failed):
    gallery.start(save)
    try:
        # Where every creature is, before anything is photographed. A wildlife mod's animals
        # wander, so a case that comes back with an empty patch of sand is ambiguous: it could be
        # the fix, or it could be that nothing was standing there. This census answers that, and
        # it is kept for both runs so the two can be compared.
        census = capture_console("radiance_creatures all",
                                 os.path.join(folder, "creatures-%s.txt" % save))
        print("  radiance_creatures all: %d lines kept" % census, flush=True)
        for case in cases:
            print("\n=== %s (%s) ===" % (case["name"], case["commit"]), flush=True)
            ok, detail = gallery.arrive(case["loc"], case["x"], case["y"], case["hour"],
                                        case["season"], case["weather"], case.get("alts"),
                                        rings=False)
            print("  spot: %s" % detail, flush=True)
            if not ok:
                failed.append((case["name"], detail))
                continue
            gallery.apply_gallery_look()
            for key, value in case.get("config", {}).items():
                gallery.cfg(key, value)
            time.sleep(5)
            size, error = gallery.dump_png(os.path.join(folder, case["name"] + ".png"))
            if error:
                failed.append((case["name"], error))
                print("  %s" % error, flush=True)
                continue
            width, height, _highlight = size
            print("  %dx%d  %s" % (width, height, detail), flush=True)
            if case.get("console"):
                lines = capture_console(case["console"],
                                        os.path.join(folder, case["name"] + ".txt"))
                print("  %s: %d lines kept" % (case["console"], lines), flush=True)
            if case.get("ab_shadows_off"):
                # The one test that tells OUR shadow from the one a mod paints for itself. A small
                # dark oval under a bird is what a laid-down silhouette looks like AND what a
                # hand-painted blob looks like, and no amount of staring separates them. With our
                # shadows switched off, anything still on the ground belongs to somebody else.
                # It is also the census of what was actually standing here, since a creature that
                # wandered off is indistinguishable from one with no shadow.
                capture_console("radiance_creatures",
                                os.path.join(folder, case["name"] + "-here.txt"))
                set_our_shadows(False)
                _size, ab_error = gallery.dump_png(
                    os.path.join(folder, case["name"] + "-noshadows.png"))
                set_our_shadows(True)
                print("  ours off: %s" % (ab_error or "captured"), flush=True)
            done.append((case["name"], detail))
    finally:
        gallery.kill_game()


if __name__ == "__main__":
    raise SystemExit(main())
