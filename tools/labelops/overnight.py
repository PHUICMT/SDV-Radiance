"""Run the whole per-mod dump unattended, restart it when it stops, and put the mod set back.

    python tools/labelops/overnight.py                  fresh dump: profiles, sweep, check, attribute
    python tools/labelops/overnight.py --resume         keep the dump that is there, finish the sweep
    python tools/labelops/overnight.py --restore-to X   which profile to apply at the end
                                                        (default ThaiSFW-YURI-Radiance)

Why a wrapper at all: run.py stops the sweep on its own abort reasons (the bridge never came up,
two empty passes in a row) and exits non-zero when any pass failed. Overnight nobody is there to
type it again. This loops run.py's sweep stage while it is still making progress, gives up only
when a whole round finished nothing new, then runs the check and attribution stages and restores
the player's own mod profile so the game is playable in the morning whatever happened.

Everything it decides is written to HF-Studio/overnight.log with the time, and a one-screen
summary to HF-Studio/overnight-summary.txt at the end: passes done, passes that never succeeded
and why, versions in the dump, and how long it all took.
"""
import argparse, json, os, subprocess, sys, time

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sweep                                            # dump_state, known_profiles

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PROFILE_DIR = os.path.join(GAME, "mod-profiles")
HFDIR = os.path.expanduser(r"~\Documents\HF-Studio")
PYTHON = sys.executable
LOG = os.path.join(HFDIR, "overnight.log")
SUMMARY = os.path.join(HFDIR, "overnight-summary.txt")
MAXIMUM_ROUNDS = 12
PAUSE_BETWEEN_ROUNDS = 20


def log(message):
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {message}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as handle:
        handle.write(line + "\n")


def run(arguments, label):
    log(f"start  {label}: {' '.join(arguments)}")
    started = time.time()
    finished = subprocess.run([PYTHON, "-u", os.path.join(HERE, "run.py"), *arguments], cwd=HERE,
                              env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    log(f"end    {label}: exit {finished.returncode} after {int(time.time() - started)}s")
    return finished.returncode == 0


def planned():
    return sweep.known_profiles()


def remaining():
    done, _ = sweep.dump_state()
    return [name for name in planned() if name not in done]


def restore(profile):
    log(f"restoring mod profile {profile}")
    sweep.kill_game()
    finished = subprocess.run([PYTHON, os.path.join(HERE, "applyprofile.py"), profile],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
    log((finished.stdout or finished.stderr).strip().splitlines()[-1] if (finished.stdout or finished.stderr) else "applyprofile said nothing")
    return finished.returncode == 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--resume", action="store_true", help="keep the current dump and finish it")
    parser.add_argument("--restore-to", default="ThaiSFW-YURI-Radiance")
    arguments = parser.parse_args()
    if not os.path.exists(os.path.join(PROFILE_DIR, f"{arguments.restore_to}.json")):
        sys.exit(f"no profile named {arguments.restore_to} to restore to; refusing to start")

    os.makedirs(HFDIR, exist_ok=True)
    whole = time.time()
    log("=" * 70)
    log("overnight per-mod dump " + ("(resume)" if arguments.resume else "(fresh)"))

    if not arguments.resume:
        # --fresh moves the old dump aside, then the profiles stage writes the Solo/Batch plan.
        if not run(["--fresh", "--redo-profiles", "--only", "profiles"], "profiles"):
            restore(arguments.restore_to)
            sys.exit("the profile plan failed; nothing was dumped")

    plan = planned()
    log(f"plan: {len(plan)} passes ({plan[0]} first, {plan[-1]} last)")

    never_succeeded = []
    for round_number in range(1, MAXIMUM_ROUNDS + 1):
        before = remaining()
        if not before:
            break
        log(f"round {round_number}: {len(before)} pass(es) to go, next {before[0]}")
        run(["--only", "sweep"], f"sweep round {round_number}")
        after = remaining()
        log(f"round {round_number}: {len(before) - len(after)} finished, {len(after)} left")
        if len(after) >= len(before):
            never_succeeded = after
            log("a whole round finished nothing new: those passes are not going to work tonight")
            break
        time.sleep(PAUSE_BETWEEN_ROUNDS)
    else:
        never_succeeded = remaining()
        log(f"stopped after {MAXIMUM_ROUNDS} rounds with {len(never_succeeded)} left")

    run(["--only", "check"], "check")
    run(["--only", "attribute"], "attribute")
    restored = restore(arguments.restore_to)

    done, versions = sweep.dump_state()
    elapsed = int(time.time() - whole)
    lines = [
        f"overnight per-mod dump finished {time.strftime('%Y-%m-%d %H:%M')}",
        f"  passes done      : {len([p for p in plan if p in done])} / {len(plan)}",
        f"  map versions     : {versions:,}",
        f"  never succeeded  : {len(never_succeeded)}"
        + (f"  ({', '.join(never_succeeded[:10])}{' ...' if len(never_succeeded) > 10 else ''})" if never_succeeded else ""),
        f"  took             : {elapsed // 3600}h{(elapsed % 3600) // 60:02d}m",
        f"  mod profile      : {arguments.restore_to} {'restored' if restored else 'NOT restored, apply it by hand'}",
        f"  dump             : {os.path.join(HFDIR, 'maps.json')}",
        f"  per-pass logs    : {os.path.join(HFDIR, 'sweep-logs')}",
    ]
    with open(SUMMARY, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    for line in lines:
        log(line)


if __name__ == "__main__":
    main()
