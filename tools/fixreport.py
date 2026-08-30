"""Put the before and after of each fix side by side, and measure the gap between them.

    python tools/fixreport.py

Reads `~/Documents/Radiance-Fixshots/before/` and `.../after/`, written by two runs of
fixshots.py against two builds, and writes `report.md` beside them.

The measurement is there because a pair of pictures is easy to look at and easy to be wrong
about: two frames of the same place can differ because a cloud moved. Every pair gets the number
of pixels that changed by more than a hair and the box those pixels sit in, so a fix that landed
where it was supposed to can be told from a frame that drifted.
"""
import os, sys

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fixshots import CASES, NOT_SHOT, OUT

CHANGED_AT = 8   # per-channel difference that counts as a changed pixel, out of 255


def compare(before_path, after_path):
    before = np.asarray(Image.open(before_path).convert("RGB")).astype(np.int16)
    after = np.asarray(Image.open(after_path).convert("RGB")).astype(np.int16)
    if before.shape != after.shape:
        return None, "the two frames are different sizes (%s against %s)" % (before.shape, after.shape)
    delta = np.abs(after - before).max(axis=2)
    mask = delta > CHANGED_AT
    count = int(mask.sum())
    if count == 0:
        return dict(count=0, box=None, peak=int(delta.max())), None
    rows = np.flatnonzero(mask.any(axis=1))
    cols = np.flatnonzero(mask.any(axis=0))
    box = (int(cols[0]), int(rows[0]), int(cols[-1]), int(rows[-1]))
    return dict(count=count, box=box, peak=int(delta.max())), None


def main():
    lines = ["# What each fix changed, in pictures", "",
             "Two builds, the same spots, the same clock and the same settings. `before/` is the "
             "branch as it stood before this run of fixes; `after/` is the current build. Each "
             "pair below was taken by `tools/fixshots.py`, which reuses the gallery's own arrival "
             "so nothing but the code differs between the two columns.", "",
             "The number under each pair is how many pixels changed by more than %d/255 and the "
             "box they sit in. A fix that landed shows a count in the thousands inside a box "
             "around the thing it fixed; a frame that merely drifted shows a scatter with no box "
             "worth naming." % CHANGED_AT, ""]
    missing = []
    for case in CASES:
        before_path = os.path.join(OUT, "before", case["name"] + ".png")
        after_path = os.path.join(OUT, "after", case["name"] + ".png")
        lines += ["## %s" % case["name"], "",
                  "`%s`" % case["commit"], "", case["look"], ""]
        if not (os.path.exists(before_path) and os.path.exists(after_path)):
            have = [side for side in ("before", "after")
                    if os.path.exists(os.path.join(OUT, side, case["name"] + ".png"))]
            lines += ["**Not captured.** Have: %s." % (", ".join(have) or "neither"), ""]
            missing.append(case["name"])
            continue
        stats, error = compare(before_path, after_path)
        lines += ["| before | after |", "|---|---|",
                  "| ![before](before/%s.png) | ![after](after/%s.png) |" % (case["name"], case["name"]),
                  ""]
        if error:
            lines += ["Could not measure the pair: %s" % error, ""]
        elif stats["count"] == 0:
            lines += ["**Nothing changed** between the two builds here (largest difference "
                      "%d/255). Either the case does not reach the code that changed, or the "
                      "wrong build was deployed for one of the runs." % stats["peak"], ""]
        else:
            x0, y0, x1, y1 = stats["box"]
            lines += ["%s pixels changed, peak %d/255, all inside x %d to %d, y %d to %d."
                      % (format(stats["count"], ","), stats["peak"], x0, x1, y0, y1), ""]
        for side in ("before", "after"):
            log = os.path.join(OUT, side, case["name"] + ".txt")
            if os.path.exists(log):
                lines += ["`%s` output, %s: [%s.txt](%s/%s.txt)"
                          % (case.get("console", "diagnostic"), side, case["name"], side, case["name"]), ""]
    lines += ["## Not shot, because a still cannot carry it", ""]
    for what, commit, why in NOT_SHOT:
        lines.append("- **%s** (`%s`) - %s" % (what, commit, why))
    lines += ["", "These want a short clip.", ""]
    if missing:
        lines += ["## Missing pairs", "",
                  "These were refused or failed on at least one of the two runs: %s. The run log "
                  "says which spot was refused and why." % ", ".join(missing), ""]
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, "report.md")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
