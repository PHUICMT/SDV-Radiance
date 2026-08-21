"""Download mods from Nexus by id and unpack them into a PARKED category folder.

They land in "Mods (disabled)", not "Mods": radiance_mapdump all reads every PNG off disk
including parked ones, so their tilesheets reach the labeller's sheet mode without any of them
being loaded by the game. Nothing about the active profile changes.

    python nexusget.py <category-folder> <id> [id ...]
    python nexusget.py 09_Buildings --from nexus-buildings.json --limit 40
"""
import io, json, os, re, subprocess, sys, time, urllib.error, urllib.parse, urllib.request, zipfile
sys.stdout.reconfigure(encoding="utf-8")

REPO = r"e:\Games\GamesMods\DevStardew\SDV-Radiance"
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
PARK = os.path.join(GAME, "Mods (disabled)")
HERE = os.path.dirname(os.path.abspath(__file__))
GAME_DOMAIN = "stardewvalley"
SEVENZIP = next((p for p in [
    os.path.join(os.environ.get("ProgramFiles", ""), "7-Zip", "7z.exe"),
    os.path.join(os.environ.get("ProgramFiles(x86)", ""), "7-Zip", "7z.exe"),
    os.path.expanduser(os.path.join("~", "scoop", "shims", "7z.exe")),
] if p and os.path.exists(p)), None)

key = None
for line in open(os.path.join(REPO, ".env"), encoding="utf-8"):
    if "NEXUS" in line and "=" in line:
        key = line.split("=", 1)[1].strip().strip('"').strip("'")
HDR = {"apikey": key, "User-Agent": "SDV-Radiance-labelplan/1.0", "Accept": "application/json"}


def api(path, tries=4):
    for a in range(tries):
        try:
            req = urllib.request.Request(f"https://api.nexusmods.com{path}", headers=HDR)
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503) and a < tries - 1:
                time.sleep(5 + a * 10)
                continue
            raise
        except Exception:
            if a == tries - 1:
                raise
            time.sleep(3)


def installed_ids():
    ids = set()
    for root in [os.path.join(GAME, "Mods"), PARK]:
        if not os.path.isdir(root):
            continue
        for dp, dns, fns in os.walk(root):
            # Walked in full: a manifest sits up to four folders below a mod's own folder
            # in this library, and a bundle legitimately carries several. A depth cap here
            # under-counts what is installed, which is how a re-download of something already
            # on disk gets planned.
            dns[:] = [d for d in dns if d not in (".git", "node_modules", "__pycache__")]
            for fn in fns:
                if fn != "manifest.json":
                    continue
                try:
                    raw = open(os.path.join(dp, fn), encoding="utf-8-sig").read()
                except Exception:
                    continue
                for m in re.finditer(r'"Nexus:\s*(\d+)"', raw):
                    ids.add(int(m.group(1)))
    return ids


def safe(name):
    out = "".join("_" if c in '<>:"/\\|?*' else c for c in name).strip(" .")
    return out[:120] or "mod"


def fetch(mod_id, dest_cat):
    info = api(f"/v1/games/{GAME_DOMAIN}/mods/{mod_id}.json")
    name = info.get("name") or f"mod{mod_id}"
    files = api(f"/v1/games/{GAME_DOMAIN}/mods/{mod_id}/files.json").get("files", [])
    # MAIN first, then whatever is newest; skip old versions and optional extras
    main = [f for f in files if (f.get("category_name") or "").upper() == "MAIN"]
    pool = main or [f for f in files if f.get("is_primary")] or files
    if not pool:
        return None, f"{name}: no downloadable file"
    f = max(pool, key=lambda x: x.get("uploaded_timestamp") or 0)
    fid, fname = f["file_id"], f.get("file_name", "")
    ext = os.path.splitext(fname)[1].lower()
    # A sizeable slice of the Buildings category ships .rar or .7z, which zipfile cannot read.
    # 7-Zip handles all three and is already installed here, so only fall over when it is not.
    if ext not in (".zip", ".rar", ".7z"):
        return None, f"{name}: {fname} is not an archive this can unpack"
    if ext != ".zip" and not SEVENZIP:
        return None, f"{name}: {fname} needs 7-Zip, which was not found"
    links = api(f"/v1/games/{GAME_DOMAIN}/mods/{mod_id}/files/{fid}/download_link.json")
    # The CDN link carries the mod's real file name, spaces and all, and urllib refuses a URL
    # with raw spaces in it ("can't contain control characters"). Percent-encode the path while
    # leaving the query's own delimiters alone.
    url = urllib.parse.quote(links[0]["URI"], safe=":/?&=%~+")
    req = urllib.request.Request(url, headers={"User-Agent": HDR["User-Agent"]})
    with urllib.request.urlopen(req, timeout=300) as r:
        blob = r.read()
    target = os.path.join(PARK, dest_cat, safe(name))
    os.makedirs(target, exist_ok=True)
    if ext == ".zip":
        with zipfile.ZipFile(io.BytesIO(blob)) as z:
            bad = [n for n in z.namelist()
                   if n.startswith("/") or ".." in n.replace("\\", "/").split("/")]
            if bad:
                return None, f"{name}: archive has unsafe paths, skipped"
            z.extractall(target)
    else:
        tmp = os.path.join(target, "__dl" + ext)
        with open(tmp, "wb") as fh:
            fh.write(blob)
        # -y assume yes, -o output dir. 7-Zip refuses traversal paths on its own.
        r = subprocess.run([SEVENZIP, "x", tmp, "-o" + target, "-y"],
                           capture_output=True, text=True, errors="replace")
        try:
            os.remove(tmp)
        except OSError:
            pass
        if r.returncode != 0:
            return None, f"{name}: 7z failed ({(r.stdout or r.stderr or '')[-120:].strip()})"
    return (name, len(blob), target), None


# A parked folder per Nexus category, so a few hundred mods stay identifiable (and removable)
# instead of being tipped into one bucket.
CAT_FOLDER = {"Buildings": "09_Buildings", "Locations": "10_Locations",
              "Maps": "11_Maps", "Visuals and Graphics": "12_Visuals"}

# Titles that cannot possibly hand the labeller a tilesheet. Measured, not guessed: of the
# first 657 mods pulled, 210 shipped no PNG that MapDump would even look at - translations,
# save files, UI and portrait packs, ReShade presets. Skipping them by name up front costs
# nothing, because a title that says "Chinese translation" is never a tilesheet.
JUNK_WORDS = (
    "translation", "translated", "localization", "localisation",
    "savegame", "save game", "save file", "reshade", "font", "portrait", "dialogue",
    "cheat", "config menu", "soundtrack", "voice pack", "subtitle",
    "hairstyle", "makeup", "wallpaper only",
)
# A LANGUAGE name is not on its own evidence of anything: "Seasonal Japanese Buildings" and
# "(AT) Chinese Coop" are real building art, and a blanket language test threw both away. Only
# the shapes a translation actually takes count - "(Spanish) X", "X in Spanish", "X - Chinese
# version". Erring towards keeping is cheap; a wrongly dropped tilesheet pack is not.
LANGS = ("chinese", "spanish", "portuguese", "russian", "korean", "japanese", "turkish",
         "italian", "german", "french", "polish", "ukrainian", "thai", "vietnamese")
LANG_RE = re.compile(
    r"\((?:%s)\)|\bin (?:%s)\b|(?:%s)\s*(?:translation|version|patch|localis|localiz)"
    % ("|".join(LANGS), "|".join(LANGS), "|".join(LANGS)), re.I)


def looks_like_junk(name):
    low = " " + name.lower() + " "
    return any(w in low for w in JUNK_WORDS) or bool(LANG_RE.search(name))


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    cat = sys.argv[1]
    args = sys.argv[2:]
    plan = []                                  # (id, folder)
    if args[0] == "--from":
        # Beside the script, or beside you, or wherever you actually said. Resolving only
        # against HERE meant a list built in one folder could not be fed to the downloader
        # from another, and the error it gave named a path nobody had typed.
        listed = args[1]
        for candidate in (listed, os.path.join(os.getcwd(), listed), os.path.join(HERE, listed)):
            if os.path.exists(candidate):
                listed = candidate
                break
        else:
            sys.exit(f"no such list: {args[1]} (looked beside you and beside {HERE})")
        rows = json.load(open(listed, encoding="utf-8"))
        limit = int(args[args.index("--limit") + 1]) if "--limit" in args else 25
        # "auto" routes each row by its own Nexus category; a literal name forces one folder.
        junked = 0
        for r in rows:
            if r.get("have"):
                continue
            if looks_like_junk(r.get("name", "")):
                junked += 1
                continue
            folder = CAT_FOLDER.get(r.get("cat", ""), cat) if cat == "auto" else cat
            plan.append((r["id"], folder))
            if len(plan) >= limit:
                break
        if junked:
            print(f"skipped {junked} by title (translations, saves, UI, portraits ...)")
    else:
        plan = [(int(a), cat) for a in args if a.isdigit()]

    have = installed_ids()
    todo = [(i, f) for i, f in plan if i not in have]
    print(f"{len(plan)} requested · {len(plan)-len(todo)} already installed · {len(todo)} to fetch")
    for f in sorted({f for _, f in todo}):
        print(f"  target: {os.path.join(PARK, f)}")
        os.makedirs(os.path.join(PARK, f), exist_ok=True)
    print()

    ok = skipped = 0
    bytes_total = 0
    for n, (mid, folder) in enumerate(todo, 1):
        try:
            got, why = fetch(mid, folder)
        except Exception as e:
            got, why = None, f"#{mid}: {e}"
        if got:
            ok += 1
            bytes_total += got[1]
            print(f"  [{n}/{len(todo)}] OK   #{mid:<6} {got[1]/1e6:6.1f} MB  {got[0][:52]}", flush=True)
        else:
            skipped += 1
            print(f"  [{n}/{len(todo)}] skip #{mid:<6} {why}", flush=True)
        time.sleep(1.0)      # be polite to the API even with premium
    print(f"\ndownloaded {ok}, skipped {skipped}, {bytes_total/1e6:.0f} MB total")


if __name__ == "__main__":
    main()
