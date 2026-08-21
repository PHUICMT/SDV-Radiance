"""One command that builds the whole label corpus, and can be stopped at any point.

    python tools/labelops/run.py

Four stages. Each is skipped if it is already done, so re-running after a crash, a reboot, or
a deliberate ctrl-C picks up exactly where it stopped:

    1  profiles    colour the mod library into passes that do not clash
    2  sweep       start the game once per pass and dump it            <- the long one
    3  check       verify the dump structurally
    4  attribute   name the mod behind each version of each place

Resume is not bolted on: the dump itself records which profiles are in it, so "what is left"
is read from the artefact rather than from a progress file that can disagree with reality.

    --only sweep         run one stage
    --from sweep         run from that stage onwards
    --redo-profiles      recolour the library (invalidates a sweep in progress: see below)
    --fresh              start a NEW dump, moving the current one aside first

Recolouring mid-sweep is refused, and that refusal is the point: passes are named MapPass-NN
by position, so recolouring after twenty passes leaves twenty names in the dump that no longer
mean what they meant. Finish the sweep, or start fresh.
"""
import argparse, json, os, shutil, subprocess, sys, time

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sweep                                       # the pass loop, so progress can be per-pass

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PROFILE_DIR = os.path.join(GAME, "mod-profiles")
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
PYTHON = sys.executable
STAGES = ("profiles", "sweep", "check", "attribute")


# ---- how it looks ----------------------------------------------------------------------------

COLOUR = sys.stdout.isatty()


def paint(text, code):
    return f"\033[{code}m{text}\033[0m" if COLOUR else text


def rule(title):
    print("\n" + paint("─" * 78, "90"))
    print(paint(f" {title}", "1;36"))
    print(paint("─" * 78, "90"), flush=True)


def clock(seconds):
    seconds = int(max(0, seconds))
    if seconds < 60:
        return f"{seconds}s"
    if seconds < 3600:
        return f"{seconds // 60}m{seconds % 60:02d}s"
    return f"{seconds // 3600}h{(seconds % 3600) // 60:02d}m"


def bar(done, total, width=28):
    if not total:
        return " " * width
    filled = int(width * done / total)
    return paint("█" * filled, "36") + paint("░" * (width - filled), "90")


def status(done, total, started, versions, label):
    """One line, rewritten in place, that answers 'how far, how long, how much'."""
    elapsed = time.time() - started
    eta = (elapsed / done) * (total - done) if done else 0
    line = (f"  {bar(done, total)} {done:>3}/{total}  "
            f"{paint(f'{versions:,}', '1;32')} versions  "
            f"{clock(elapsed)} elapsed  {paint('eta ' + clock(eta), '33')}  {label}")
    if COLOUR:
        sys.stdout.write("\r\033[K" + line)
        sys.stdout.flush()
    else:
        print(line, flush=True)


# ---- what is done already ---------------------------------------------------------------------

def profiles_on_disk():
    if not os.path.isdir(PROFILE_DIR):
        return []
    return sweep.in_pass_order(os.path.splitext(f)[0] for f in os.listdir(PROFILE_DIR)
                              if f.startswith("MapPass-") and f.endswith(".json"))


def run_stage(name, script, arguments=()):
    rule(f"{name}")
    finished = subprocess.run([PYTHON, "-u", os.path.join(HERE, script), *arguments],
                              cwd=HERE, env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    return finished.returncode == 0


def move_aside():
    """Start a new dump without destroying the last one. Labels are not touched: they live in
    labels/ and are keyed by sheet name, which no dump decides."""
    stamp = time.strftime("%Y%m%d-%H%M%S")
    target = os.path.join(HFDIR, f"_dump-{stamp}")
    os.makedirs(target, exist_ok=True)
    moved = []
    for item in ("maps.json", "maps", "sheets"):
        source = os.path.join(HFDIR, item)
        if os.path.exists(source):
            shutil.move(source, os.path.join(target, item))
            moved.append(item)
    print(f"  moved {', '.join(moved) or 'nothing'} to {os.path.basename(target)}")
    print("  labels/ untouched: labels are keyed by sheet name, which no dump decides")


# ---- the long stage ----------------------------------------------------------------------------

def do_sweep():
    wanted = profiles_on_disk()
    if not wanted:
        print(paint("  no MapPass profiles: run the profiles stage first", "31"))
        return False
    wanted = (["Label-BaseArt"] if os.path.exists(os.path.join(PROFILE_DIR, "Label-BaseArt.json"))
              else []) + wanted
    done, versions = sweep.dump_state()
    todo = [p for p in wanted if p not in done]

    print(f"  {len(wanted)} pass(es) in the plan, {len(wanted) - len(todo)} already in the dump, "
          f"{len(todo)} to run")
    if not todo:
        print(paint("  the sweep is complete", "32"))
        return True
    if not os.path.exists(os.path.join(PROFILE_DIR, "_before-sweep.json")):
        sweep.snapshot("_before-sweep")

    started = time.time()
    completed = len(wanted) - len(todo)
    failed = []
    for profile in todo:
        status(completed, len(wanted), started, versions, f"starting {profile}")
        # Only the first pass of THIS RUN reads every PNG on disk. What that finds does not
        # depend on which mods are loaded, so doing it on every pass is the same several minutes
        # of reading for a result that cannot change.
        #
        # Keyed on the run, not on the dump being empty: with one pass already recorded, the old
        # test was false from the very first pass onwards and the disk sweep never happened at
        # all - which loses exactly the art it exists for, the bare tilesheet packs that ship no
        # maps and are the most water-dense and bridge-dense art in the mod scene.
        first = (profile == todo[0])
        if COLOUR:
            print()
        ok = sweep.run_profile(profile, all_sheets=first)
        completed += 1
        done, versions = sweep.dump_state()
        if sweep.ABORT_REASON:
            print(paint(f"\n  stopping the whole sweep: {sweep.ABORT_REASON}", "31"))
            sweep.kill_game()
            return False
        if not ok:
            failed.append(profile)
        status(completed, len(wanted), started, versions, paint("ok" if ok else "FAILED",
                                                               "32" if ok else "31"))
        # Nothing at all after two passes is not two bad passes, it is a broken run. The first
        # time this mattered, eight passes went by with "0 versions" on the progress line and the
        # loop was cheerfully planning ninety-four more.
        if versions == 0 and len(failed) >= 2:
            print(paint("\n  stopping: two passes have run and the dump is still empty.\n"
                        "  That is the run being broken, not those two passes. The reason is in\n"
                        f"  {os.path.join(HFDIR, 'sweep-logs')} and on the FAILED lines above.", "31"))
            sweep.kill_game()
            return False
    if COLOUR:
        print()
    sweep.kill_game()
    if failed:
        print(paint(f"  {len(failed)} pass(es) failed: {', '.join(failed[:6])}", "31"))
        print("  run this command again: passes already in the dump are skipped")
        return False
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", choices=STAGES)
    parser.add_argument("--from", dest="start", choices=STAGES)
    parser.add_argument("--redo-profiles", action="store_true")
    parser.add_argument("--fresh", action="store_true", help="move the current dump aside first")
    arguments = parser.parse_args()

    if arguments.only:
        plan = [arguments.only]
    elif arguments.start:
        plan = list(STAGES[STAGES.index(arguments.start):])
    else:
        plan = list(STAGES)

    rule("SDV-Radiance label corpus")
    done, versions = sweep.dump_state()
    print(f"  dump      : {versions:,} map version(s) from {len(done)} pass(es)")
    print(f"  profiles  : {len(profiles_on_disk())} MapPass on disk")
    print(f"  stages    : {' -> '.join(plan)}")

    if arguments.fresh:
        rule("moving the current dump aside")
        move_aside()
        done, versions = set(), 0

    whole_run = time.time()
    for stage in plan:
        if stage == "profiles":
            if profiles_on_disk() and not arguments.redo_profiles:
                if done:
                    # Passes are named by POSITION, so recolouring after twenty of them leaves
                    # twenty names in the dump that no longer mean what they meant.
                    print(paint("\n  profiles: kept. A sweep is already part done and passes are "
                                "named by position,\n            so recolouring would rename work "
                                "that is already in the dump.\n            Finish it, or start "
                                "over with --fresh --redo-profiles.", "33"))
                else:
                    print(paint("\n  profiles: kept (pass --redo-profiles to recolour)", "90"))
                continue
            if done and not arguments.fresh:
                sys.exit(paint("refusing to recolour: the dump already holds "
                               f"{len(done)} pass(es) named by position. Use --fresh.", "31"))
            if not run_stage("1  profiles - colouring the library into passes", "genmapprofiles.py"):
                sys.exit("the colouring failed")

        elif stage == "sweep":
            rule("2  sweep - one game session per pass")
            if not do_sweep():
                sys.exit(1)

        elif stage == "check":
            if not run_stage("3  check - is the dump what it says it is", "checkdump.py", ["--deep"]):
                print(paint("  the dump has problems: read them before labelling against it", "31"))
            # And the one thing a structural check cannot see: whether a tile that is TURNED on
            # the map was recorded turned the same way. Decoded independently from the .tmx gid
            # bits, because reading them from the same code that wrote them proves nothing. This
            # was wrong once and silently: three of the seven flip/rotate combinations were being
            # dropped.
            run_stage("3b check - tile orientation against the .tmx it came from", "verifydump.py")

        elif stage == "attribute":
            run_stage("4  attribute - which mod made each version", "whoowns.py")

    done, versions = sweep.dump_state()
    rule("done")
    print(f"  {versions:,} map version(s) from {len(done)} pass(es) in {clock(time.time() - whole_run)}")
    print(f"  {os.path.join(HFDIR, 'maps.json')}")
    print("  put the mod set back with:  python tools/labelops/applyprofile.py _before-sweep")


if __name__ == "__main__":
    main()
