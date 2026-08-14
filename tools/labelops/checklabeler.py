"""Load-order audit for the labeler's split scripts.

They are classic <script> tags in a fixed order (deliberately, so the tool still runs from a
file:// URL where ES modules are blocked). That makes ORDER load-bearing: anything a file
touches while it is being evaluated must already exist. A function body is fine - it runs
later - but a top-level call, or a const initialised from another file's value, is not.

Reports, in order of how badly each would break the page:
  1. top-level use of an identifier declared in a LATER file  (ReferenceError at boot)
  2. identifiers used nowhere declared                        (typo or a lost declaration)
  3. the same name declared in two files                      (one silently wins)
"""
import os, re, sys
from collections import defaultdict
sys.stdout.reconfigure(encoding="utf-8")

ROOT = r"e:\Games\GamesMods\DevStardew\SDV-HeightFramework\tools\labeler"
HTML = os.path.join(ROOT, "index.html")

# index.html loads only the bootstrap; the app modules are chained by APP_SCRIPTS in 00-boot.js,
# one at a time on onload, so THAT array is the real load order.
order = [s for s in re.findall(r'<script[^>]+src="([^"]+)"',
                               open(HTML, encoding="utf-8").read()) if s.endswith(".js")]
boot = open(os.path.join(ROOT, "js", "00-boot.js"), encoding="utf-8").read()
chain = re.search(r"APP_SCRIPTS\s*=\s*\[(.*?)\]", boot, re.S)
if chain:
    order += re.findall(r'"([^"]+\.js)"', chain.group(1))
print("script order in index.html:")
for i, s in enumerate(order):
    print(f"  {i:2d} {s}")
disk = sorted(f for f in os.listdir(os.path.join(ROOT, "js")) if f.endswith(".js"))
listed = [os.path.basename(s) for s in order]
missing = [f for f in disk if f not in listed]
if missing:
    print(f"\n!! on disk but NOT loaded by index.html: {missing}")
if listed != sorted(listed):
    print(f"\n!! load order is not the numeric filename order: {listed}")


def strip(src):
    """Remove comments and string/template/regex literals so they cannot fake a reference."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        two = src[i:i+2]
        if two == "//":
            j = src.find("\n", i); i = n if j < 0 else j; continue
        if two == "/*":
            j = src.find("*/", i+2); i = n if j < 0 else j+2; continue
        if c in "\"'`":
            q, j = c, i+1
            while j < n:
                if src[j] == "\\":
                    j += 2; continue
                if src[j] == q:
                    break
                j += 1
            out.append(' ' * (j - i + 1)); i = j+1; continue
        out.append(c); i += 1
    return "".join(out)


DECL = re.compile(r"\b(?:function|class)\s+([A-Za-z_$][\w$]*)|"
                  r"\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)")
IDENT = re.compile(r"\b([A-Za-z_$][\w$]*)\b")
KEYWORDS = set("""await break case catch class const continue debugger default delete do else
export extends finally for function if import in instanceof let new return static super switch
this throw try typeof var void while with yield true false null undefined of async get set""".split())
GLOBALS = set("""window document console Math JSON Object Array String Number Boolean Date RegExp
Map Set WeakMap Promise Error TypeError localStorage indexedDB setTimeout setInterval
clearInterval clearTimeout requestAnimationFrame Image Uint8Array Uint8ClampedArray Int32Array
Float32Array atob btoa alert confirm prompt navigator location fetch URL Blob FileReader
performance structuredClone crypto CustomEvent Event MouseEvent KeyboardEvent isNaN parseInt
parseFloat encodeURIComponent decodeURIComponent Intl Symbol Proxy Reflect BigInt globalThis
history screen top self frames name length arguments eval""".split())

decls, top_uses, all_uses, paths, kinds = {}, defaultdict(set), defaultdict(set), {}, {}
for rel in order:
    path = os.path.join(ROOT, rel.replace("/", os.sep))
    src = strip(open(path, encoding="utf-8").read())
    fname = os.path.basename(rel)
    paths[fname] = path
    # Walk line by line tracking nesting: only depth 0 is GLOBAL scope and only depth 0 runs
    # as the file loads. A `const x` inside a function is a local and means nothing here.
    depth = 0
    for line in src.splitlines():
        if depth == 0:
            for m in DECL.finditer(line):
                name = m.group(1) or m.group(2)
                decls.setdefault(name, []).append(fname)
                kind = "const" if re.search(r"\bconst\s+" + re.escape(name), line) else \
                       ("let" if re.search(r"\blet\s+" + re.escape(name), line) else "other")
                kinds[(name, fname)] = kind
            for m in IDENT.finditer(line):
                top_uses[fname].add(m.group(1))
        for m in IDENT.finditer(line):
            all_uses[fname].add(m.group(1))
        opens = line.count("{") + line.count("(") + line.count("[")
        closes = line.count("}") + line.count(")") + line.count("]")
        depth = max(0, depth + opens - closes)

pos = {os.path.basename(s): i for i, s in enumerate(order)}
print("\n=== 1. TOP-LEVEL use of something declared LATER (breaks at boot) ===")
bad = 0
for f, names in top_uses.items():
    for nm in sorted(names):
        if nm in KEYWORDS or nm in GLOBALS or nm not in decls:
            continue
        first = min(pos[d] for d in decls[nm])
        if first > pos[f]:
            print(f"  {f} uses '{nm}' declared in {decls[nm]} (loads later)")
            bad += 1
print("  none" if not bad else f"  {bad} found")

print("\n=== 2. used but never declared anywhere (typo / lost declaration) ===")
seen = set()
for f, names in all_uses.items():
    for nm in sorted(names):
        if nm in KEYWORDS or nm in GLOBALS or nm in decls or nm in seen:
            continue
        # property accesses and object keys produce a lot of noise; only flag CALLS
        src = strip(open(paths[f], encoding="utf-8").read())
        if re.search(r"(?<![.\w$])" + re.escape(nm) + r"\s*\(", src):
            print(f"  {f}: {nm}(...)")
            seen.add(nm)
print("  none" if not seen else f"  {len(seen)} found")

print("\n=== 3. same name declared in more than one file ===")
dup = {k: v for k, v in decls.items() if len(set(v)) > 1}
for k, v in sorted(dup.items()):
    ks = {kinds.get((k, f), "other") for f in set(v)}
    fatal = "  <-- SyntaxError: const/let redeclared" if ks & {"const", "let"} else ""
    print(f"  {k}: {sorted(set(v))}{fatal}")
print("  none" if not dup else f"  {len(dup)} found")
