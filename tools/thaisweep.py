r"""No Thai in code. Prints every offending file:line, and exits 1 when there are any.

The command the docs give for this, `grep -rlP '[\x{0E00}-\x{0E7F}]' src shaders`, does not work
in this shell: it exits 2 with "character value in \x{} or \o{} is too large" and prints nothing,
which looks exactly like passing. Every commit that "passed the sweep" had in fact never run one.

    python tools/thaisweep.py [path ...]        (default: src shaders tools)
"""
import io, os, sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT = ("src", "shaders", "tools")
CODE = {".cs", ".fx", ".fxh", ".py", ".js", ".ps1", ".hlsl", ".csproj", ".props", ".targets"}


THAI_FIRST, THAI_LAST = 0x0E00, 0x0E7F


def thai(text):
    # By code point, not by literal characters: writing the range ends as characters put two
    # Thai characters in this file and the sweep reported itself.
    return any(THAI_FIRST <= ord(c) <= THAI_LAST for c in text)


def main():
    roots = sys.argv[1:] or DEFAULT
    hits, scanned = [], 0
    for root in roots:
        base = root if os.path.isabs(root) else os.path.join(REPO, root)
        if os.path.isfile(base):
            walk = [(os.path.dirname(base), [], [os.path.basename(base)])]
        else:
            walk = os.walk(base)
        for dirpath, dirnames, filenames in walk:
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", "__pycache__", ".git")]
            for filename in filenames:
                if os.path.splitext(filename)[1].lower() not in CODE:
                    continue
                path = os.path.join(dirpath, filename)
                scanned += 1
                try:
                    lines = io.open(path, encoding="utf-8", errors="replace").read().splitlines()
                except OSError:
                    continue
                for number, line in enumerate(lines, 1):
                    if thai(line):
                        hits.append((os.path.relpath(path, REPO), number, line.strip()[:100]))

    for path, number, line in hits:
        print(f"{path}:{number}: {line}")
    print(f"\n{scanned} code file(s) scanned, {len(hits)} line(s) with Thai in them")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main())
