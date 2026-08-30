# Publish to the SDV-Radiance Nexus page (mods/49397) without hand-copying URLs.
#
# The Nexus API is read only: it cannot upload an image and it cannot edit a
# description, so this drives a real Edge browser through Playwright, reusing the
# logged-in profile that tools/nexus-inbox.py already keeps.
#
# Usage:
#   python tools/nexus-publish.py session check      # is the new site signed in?
#   python tools/nexus-publish.py session save       # back the cookies up out of the profile
#   python tools/nexus-publish.py session restore    # put them back after a profile wipe
#   python tools/nexus-publish.py probe              # describe the edit pages (writes nothing)
#   python tools/nexus-publish.py upload FILES...    # dry run: says what it would upload
#   python tools/nexus-publish.py upload FILES... --confirm
#   python tools/nexus-publish.py description DRAFT.md            # diff against the live page
#   python tools/nexus-publish.py description DRAFT.md --confirm  # write it
#
# Nothing here writes to the public page without --confirm. Without it every write
# command prints exactly what it would do and stops.

import argparse
import io
import json
import os
import re
import sys
import time
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image
from playwright.sync_api import sync_playwright

GAME = "stardewvalley"
MOD_ID = 49397
BASE = "https://www.nexusmods.com"
MOD_URL = f"{BASE}/{GAME}/mods/{MOD_ID}"

REPO = Path(__file__).resolve().parent.parent
# Shared with tools/nexus-inbox.py on purpose: one sign-in serves both tools.
PROFILE_DIR = Path(os.environ["LOCALAPPDATA"]) / "radiance-nexus-inbox" / "edge-profile"
# Real account credentials. docs/local/ is gitignored and .gitignore also carries a
# *.local.json rule so a fresh clone cannot commit this by accident.
SESSION_FILE = REPO / "docs" / "local" / "nexus-session.local.json"
PROBE_DIR = REPO / "docs" / "local" / "nexus-inbox" / "probe"

SIGNED_OUT_TEXT = r"Please log in again|session has expired|You need to \*\*log in\*\*"


def launch(playwright, headed=True):
    """Open the shared profile. Always headed: Cloudflare blocks headless."""
    ctx = playwright.chromium.launch_persistent_context(
        str(PROFILE_DIR),
        channel="msedge",
        headless=not headed,
        viewport={"width": 1500, "height": 1000},
        args=["--disable-blink-features=AutomationControlled"],
        ignore_default_args=["--enable-automation", "--no-sandbox"],
    )
    ctx.set_default_timeout(45000)
    return ctx


def settle(page, seconds=6):
    """Wait out Cloudflare and let the new site's client-side app render.

    The new Nexus pages are client rendered: reading the DOM at domcontentloaded
    finds an empty shell and reports every field missing.
    """
    deadline = time.time() + 120
    while time.time() < deadline:
        try:
            title = (page.title() or "").lower()
        except Exception:
            time.sleep(1)
            continue
        if "just a moment" not in title and "attention required" not in title:
            break
        print("  Cloudflare challenge - tick the box in the Edge window if it stays", flush=True)
        time.sleep(2)
    time.sleep(seconds)


def signed_in(page):
    """True when the page is not showing the signed-out interstitial."""
    try:
        return not page.evaluate(
            "(re) => new RegExp(re, 'i').test(document.body.innerText.slice(0, 3000))",
            SIGNED_OUT_TEXT)
    except Exception:
        return False


def wait_for_human_signin(page, url):
    """Navigate ONCE, then wait without touching the browser.

    Re-issuing goto() while the user is being redirected through
    users.nexusmods.com cancels their sign-in and drops them back on the expired
    page, which looks exactly like the login failing. That mistake cost two rounds.
    """
    try:
        page.goto(url, wait_until="domcontentloaded")
    except Exception:
        pass
    settle(page, 3)
    if signed_in(page) and "users.nexusmods.com" not in page.url:
        return True

    print("\n  NOT SIGNED IN on the new Nexus site.", flush=True)
    print("  Sign in in the Edge window. I will not navigate while you do.", flush=True)
    deadline = time.time() + 900
    was_on_login = False
    while time.time() < deadline:
        time.sleep(3)
        try:
            here = page.url
        except Exception:
            continue
        if "users.nexusmods.com" in here:
            was_on_login = True
            continue
        # Only believe it once we have come back OFF the login page and the
        # interstitial is gone, or the check fires on the page we started from.
        if was_on_login and signed_in(page):
            settle(page, 3)
            if signed_in(page):
                print("  signed in", flush=True)
                return True
    return False


# ---------- session ----------

def save_session(ctx):
    SESSION_FILE.parent.mkdir(parents=True, exist_ok=True)
    state = ctx.storage_state()
    SESSION_FILE.write_text(json.dumps(state, indent=2), encoding="utf-8")
    cookies = state.get("cookies", [])
    nexus = [c for c in cookies if "nexusmods" in c.get("domain", "")]
    print(f"saved {len(cookies)} cookies ({len(nexus)} nexusmods) to {SESSION_FILE}")
    return len(nexus)


def restore_session(ctx):
    if not SESSION_FILE.exists():
        print(f"no backup at {SESSION_FILE}")
        return False
    state = json.loads(SESSION_FILE.read_text(encoding="utf-8"))
    ctx.add_cookies(state.get("cookies", []))
    print(f"pushed {len(state.get('cookies', []))} cookies back into the profile")
    return True


# ---------- probe ----------

DESCRIBE_JS = """
() => {
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const desc = el => {
    const bits = [el.tagName.toLowerCase()];
    if (el.id) bits.push('#' + el.id);
    if (el.name) bits.push('[name=' + el.name + ']');
    if (el.type) bits.push('[type=' + el.type + ']');
    for (const a of el.getAttributeNames())
      if (a.startsWith('data-testid') || a === 'aria-label' || a === 'role')
        bits.push('[' + a + '="' + el.getAttribute(a) + '"]');
    const cls = typeof el.className === 'string' ? el.className.trim() : '';
    if (cls) bits.push('.' + cls.split(/\\s+/).slice(0, 5).join('.'));
    return bits.join('');
  };
  const text = document.body ? document.body.innerText : '';
  return {
    url: location.href,
    title: document.title,
    fileInputs: [...document.querySelectorAll('input[type=file]')].map(i => ({
      sel: desc(i), accept: i.accept, multiple: i.multiple, visible: vis(i),
    })),
    textareas: [...document.querySelectorAll('textarea')].map(t => ({
      sel: desc(t), visible: vis(t), length: (t.value || '').length,
      head: (t.value || '').slice(0, 300),
    })),
    contentEditables: [...document.querySelectorAll('[contenteditable="true"]')].map(e => ({
      sel: desc(e), length: e.innerText.length, head: e.innerText.slice(0, 300),
    })),
    buttons: [...document.querySelectorAll('button,a[role=button],input[type=submit]')]
      .map(b => ({ sel: desc(b), text: (b.textContent || b.value || '').trim().slice(0, 40),
                   visible: vis(b) }))
      .filter(b => b.text && b.visible).slice(0, 60),
    editLinks: [...document.querySelectorAll('a[href]')]
      .map(a => ({ text: a.textContent.trim().slice(0, 40), href: a.href }))
      .filter(a => /\\/edit(\\/|$|\\?)/.test(a.href)).slice(0, 40),
    galleryImages: [...document.querySelectorAll('img')].map(i => i.src)
      .filter(s => /staticdelivery/.test(s)).slice(0, 60),
    bodyHead: text.replace(/\\s+/g, ' ').slice(0, 500),
  };
}
"""


def describe(page, label):
    try:
        info = page.evaluate(DESCRIBE_JS)
    except Exception as exc:
        return {"label": label, "error": str(exc)}
    info["label"] = label
    print(f"  {info['url']}")
    print(f"    file inputs {len(info['fileInputs'])} | textareas {len(info['textareas'])} | "
          f"editables {len(info['contentEditables'])} | gallery imgs {len(info['galleryImages'])}")
    for f in info["fileInputs"]:
        print(f"    FILE  {f['sel']} accept={f['accept']!r} multiple={f['multiple']} visible={f['visible']}")
    for t in info["textareas"]:
        print(f"    TEXT  {t['sel']} len={t['length']} visible={t['visible']}")
    for e in info["contentEditables"]:
        print(f"    EDIT  {e['sel']} len={e['length']}")
    return info


def probe(ctx):
    PROBE_DIR.mkdir(parents=True, exist_ok=True)
    page = ctx.pages[0] if ctx.pages else ctx.new_page()

    if not wait_for_human_signin(page, MOD_URL):
        print("still signed out, giving up")
        return 1

    # Discover the edit shell from the mod page rather than guessing URLs: guessing
    # produced a 404 for /edit/description and a signed-out render for /edit/media.
    print("\n== mod page")
    home = describe(page, "mod-page")
    targets = {}
    for link in home.get("editLinks", []):
        targets.setdefault(link["href"].split("#")[0], link["text"])
    if not targets:
        print("  no /edit link on the mod page; opening the edit root directly")
        targets[f"{BASE}/games/{GAME}/mods/{MOD_ID}/edit"] = "edit root"

    report = [home]
    visited = set()
    queue = list(targets.items())
    while queue:
        url, label = queue.pop(0)
        if url in visited:
            continue
        visited.add(url)
        print(f"\n== {label or url}")
        try:
            page.goto(url, wait_until="domcontentloaded")
        except Exception as exc:
            print(f"  navigation failed: {exc}")
            continue
        settle(page)
        if not signed_in(page):
            print("  page reports signed out")
        info = describe(page, label or url)
        report.append(info)
        safe = "".join(c if c.isalnum() else "-" for c in url)[-90:]
        (PROBE_DIR / f"{safe}.html").write_text(page.content(), encoding="utf-8")
        # Follow the edit shell's own nav once, so every tab gets described.
        for link in info.get("editLinks", []):
            clean = link["href"].split("#")[0]
            if clean not in visited:
                queue.append((clean, link["text"]))

    out = PROBE_DIR / "report.json"
    out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\nwrote {out}")
    return 0


# ---------- images ----------

MEDIA_URL = f"{BASE}/games/{GAME}/mods/{MOD_ID}/edit/media"
GENERAL_URL = f"{BASE}/games/{GAME}/mods/{MOD_ID}/edit/general"
IMAGE_DIR = REPO / "docs" / "local" / "description" / "images"
INDEX_FILE = IMAGE_DIR / "INDEX.local.md"


def mod_version():
    """The release being published, from the one file that already has to be right."""
    try:
        return json.loads((REPO / "manifest.json").read_text(encoding="utf-8"))["Version"]
    except Exception as exc:
        raise SystemExit(f"cannot read Version from manifest.json: {exc}")


def url_map_path(version):
    """One map per release: an image belongs to the release it was shot for, and last
    release's map must still be readable when this one is being put together."""
    return IMAGE_DIR / f"urls-{version}.local.json"
IMAGE_SIZE_LIMIT = 8 * 1024 * 1024              # the dropzone says ".jpg, .png, .gif; 8MB"

GALLERY_URLS_JS = """
() => [...document.querySelectorAll('a[href*="staticdelivery"]')].map(a => a.href)
"""


def gallery_urls(page):
    try:
        return set(page.evaluate(GALLERY_URLS_JS))
    except Exception:
        return set()


def click_save(page):
    """Click the first Save the page has left enabled.

    Nexus marks a dead Save with a class, not the disabled attribute, so
    is_disabled() answers False for both and cannot be used to tell them apart.
    """
    buttons = page.locator("button.nxm-button", has_text="Save")
    for i in range(buttons.count()):
        button = buttons.nth(i)
        try:
            classes = button.get_attribute("class") or ""
            if "nxm-button-disabled" in classes or not button.is_visible():
                continue
            button.click()
            return True
        except Exception:
            continue
    return False


def picture_signature(image):
    """A small, contrast-normalised fingerprint of what a picture looks like.

    Sixteen by sixteen greyscale, mean removed and scaled to unit length, so two
    signatures compare with a dot product: 1 is the same picture, and anything from a
    different scene falls away fast. Nexus re-encodes every upload (a 612 KB PNG comes
    back as 137 KB), so the bytes never match and only the picture itself can say
    which gallery URL is which local file.
    """
    small = image.convert("L").resize((16, 16), Image.Resampling.BILINEAR)
    values = np.asarray(small, dtype=np.float32)
    values -= values.mean()
    length = float(np.linalg.norm(values))
    return values / length if length else values


# One download per gallery URL per run: the retry loop asks again every few seconds
# and the gallery is already 38 pictures deep.
_signature_cache = {}


def fetch_signature(url):
    if url in _signature_cache:
        return _signature_cache[url]
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(request, timeout=90) as response:
            _signature_cache[url] = picture_signature(Image.open(io.BytesIO(response.read())))
        return _signature_cache[url]
    except Exception as exc:
        print(f"  could not read {url.rsplit('/', 1)[-1]}: {exc}")
        return None


FAILED_UPLOADS_JS = """
() => [...document.querySelectorAll('*')]
  .filter(el => el.children.length === 0 && /upload failed/i.test(el.textContent || ''))
  .map(el => (el.parentElement || el).innerText.replace(/\\s+/g, ' ').trim())
  .filter((v, i, a) => a.indexOf(v) === i)
  .slice(0, 30)
"""


# How sure a picture match has to be before a URL is written down against a file.
# The proving run scored 0.9997 for the right pair against 0.9311 for the closest
# wrong one (two screenshots of the same menu), and 1.0000 against 0.0377 for a pair
# from different scenes. A floor plus a margin over the runner-up refuses the case
# these numbers cannot cover: a file that was never uploaded at all, whose "best"
# match is simply the least unlike picture on the page.
MATCH_FLOOR = 0.60
MATCH_MARGIN = 0.03


def match_gallery(page, plan):
    """Which of these local files are in the gallery already, and at which URL.

    Nothing in the page ties a gallery URL back to the file it came from, so the
    pictures are compared to each other. Asked of the WHOLE gallery rather than of
    URLs that appeared since a snapshot, so an interrupted run, or one where
    Cloudflare lost a few uploads, still gets a straight answer.
    """
    urls = sorted(gallery_urls(page))
    print(f"  reading {len(urls)} gallery image(s) to compare against {len(plan)} file(s)")
    remote = {}
    for url in urls:
        signature = fetch_signature(url)
        if signature is not None:
            remote[url] = signature
    local = {path.stem: picture_signature(Image.open(path)) for path, _ in plan}

    # Every pairing scored, then claimed best first, so one picture cannot be filed
    # against two files and the strongest evidence wins the ties.
    scores = sorted(
        ((float((local[stem] * signature).sum()), stem, url)
         for stem in local for url, signature in remote.items()),
        reverse=True)
    found, taken_urls, best_for = {}, set(), {}
    for score, stem, url in scores:
        best_for.setdefault(stem, []).append(score)
        if stem in found or url in taken_urls:
            continue
        runner_up = next((s for s, st, u in scores
                          if st == stem and u != url and u not in taken_urls), 0.0)
        if score < MATCH_FLOOR or score - runner_up < MATCH_MARGIN:
            continue
        found[stem] = url
        taken_urls.add(url)

    missing = [path for path, _ in plan if path.stem not in found]
    return found, missing


def record(found, plan, version):
    path = url_map_path(version)
    existing = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    existing.update(found)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(existing, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"wrote {len(found)} URL(s) to {path}")
    append_index_rows(existing, [p.name for p, _ in plan if p.stem in found], version)


def report_failures(page):
    """Name the files Nexus refused, instead of letting them look like a slow upload."""
    try:
        toasts = page.evaluate(FAILED_UPLOADS_JS)
    except Exception:
        return []
    for toast in toasts:
        print(f"  UPLOAD FAILED: {toast}")
    return toasts


def stage_images(ctx, plan, version, rounds=3):
    """Hand the files to the page and stop, leaving the Save click to the author.

    Nexus holds an uploaded image as pending until Save, so nothing is public while
    this waits. It watches for the save rather than performing it, then works out
    which URL is which file only once they exist.

    Cloudflare in front of the upload endpoint returns a 520 often enough that a run
    of a dozen large PNGs usually loses one. That has to be a named file and a retry,
    not a count that never completes: waiting for "all of them" turns one lost upload
    into an hour of silence and then a wrong answer.
    """
    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    if not wait_for_human_signin(page, MEDIA_URL):
        print("not signed in")
        return 1

    remaining = list(plan)
    for attempt in range(1, rounds + 1):
        page.goto(MEDIA_URL, wait_until="domcontentloaded")
        settle(page)
        print(f"\n--- round {attempt}: handing over {len(remaining)} file(s)")
        try:
            page.set_input_files('input[type=file][accept*="image/png"]',
                                 [str(path) for path, _ in remaining])
        except Exception as exc:
            print(f"could not hand the files over: {exc}")
            return 1
        time.sleep(6)
        report_failures(page)

        print(f"\n{len(remaining)} image(s) are PENDING. Nothing is public yet.")
        print("Look at them in the Edge window, then press Save there yourself.")
        print("If one says 'Upload failed', close that toast and remove it from the")
        print("pending list before saving; I will re-add it on the next round.\n")

        found, missing = None, None
        settled_at = None
        deadline = time.time() + 3600
        while time.time() < deadline:
            time.sleep(15)
            report_failures(page)
            found, missing = match_gallery(page, plan)
            if found is None:
                return 1
            print(f"  on the page: {len(found)}/{len(plan)}", flush=True)
            if not missing:
                break
            # Stop waiting once the count has held still for a minute: that is a save
            # that finished with some files lost, not a save still in progress.
            if settled_at is None or len(found) != settled_at[0]:
                settled_at = (len(found), time.time())
            elif time.time() - settled_at[1] > 60 and len(found) > 0:
                break

        if found:
            record(found, plan, version)
        if not missing:
            print(f"\nall {len(plan)} image(s) are on the page and written down")
            return 0
        print(f"\n{len(missing)} still missing: {', '.join(p.name for p in missing)}")
        if attempt == rounds:
            break
        print("retrying those, and only those")
        remaining = [(path, size) for path, size in plan if path in missing]

    print("give the failed ones another run when Nexus settles down")
    return 1


def read_plan(files):
    """The files, with their byte counts, or None if one cannot go up as it is."""
    plan = []
    for name in files:
        path = Path(name).expanduser().resolve()
        if not path.exists():
            print(f"missing: {path}")
            return None
        size = path.stat().st_size
        if size > IMAGE_SIZE_LIMIT:
            print(f"too big for Nexus ({size / 1e6:.1f} MB > 8 MB): {path}")
            return None
        plan.append((path, size))
    return plan


def match_only(ctx, files, version):
    """Read the gallery and say which of these pictures are already up there.

    The recovery path: a run that was interrupted, or one where Cloudflare lost a
    few uploads, leaves no record of what did land. This asks the page instead of
    the notes.
    """
    plan = read_plan(files)
    if plan is None:
        return 1
    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    if not wait_for_human_signin(page, MEDIA_URL):
        print("not signed in")
        return 1
    page.goto(MEDIA_URL, wait_until="domcontentloaded")
    settle(page)
    found, missing = match_gallery(page, plan)
    if found is None:
        return 1
    for path, _size in plan:
        url = found.get(path.stem)
        print(f"  {path.name:28s} {url.rsplit('/', 1)[-1] if url else 'NOT ON THE PAGE'}")
    if found:
        record(found, plan, version)
    print(f"\n{len(found)}/{len(plan)} on the page, {len(missing)} missing")
    return 0 if not missing else 1


GALLERY_ITEMS_JS = """
() => [...document.querySelectorAll('li')]
  .map((li, index) => {
    const link = li.querySelector('a[href*="staticdelivery"]');
    const menu = li.querySelector('button[id^="headlessui-menu-button"]');
    const label = li.querySelector('p');
    return link && menu
      ? { index, url: link.href, menuId: menu.id, title: label ? label.textContent.trim() : '' }
      : null;
  })
  .filter(Boolean)
"""


def drag_item(page, handles, source_index, target_index):
    """Move one gallery picture to another slot with the mouse.

    dnd-kit reads movement, not a jump: a single mouse.move to the destination is
    read as no drag at all, and the keyboard sensor this grid ships with does not
    respond to Space and arrows, so a real dragged path is the only thing that works.
    """
    source = handles.nth(source_index)
    destination = handles.nth(target_index)
    source.scroll_into_view_if_needed()
    time.sleep(0.4)
    start = source.bounding_box()
    end = destination.bounding_box()
    if not start or not end:
        return False
    page.mouse.move(start["x"] + start["width"] / 2, start["y"] + start["height"] / 2)
    page.mouse.down()
    time.sleep(0.25)
    steps = 25
    for step in range(1, steps + 1):
        page.mouse.move(
            start["x"] + start["width"] / 2 + (end["x"] - start["x"]) * step / steps,
            start["y"] + start["height"] / 2 + (end["y"] - start["y"]) * step / steps)
        time.sleep(0.03)
    time.sleep(0.5)
    page.mouse.up()
    time.sleep(1.2)
    return True


def visible_handles(page, handles):
    """Which drag handles are on screen right now, by index."""
    height = page.viewport_size["height"] if page.viewport_size else 1000
    on_screen = []
    for index in range(handles.count()):
        box = handles.nth(index).bounding_box()
        if box and 0 <= box["y"] <= height - box["height"]:
            on_screen.append(index)
    return on_screen


def reorder_gallery(ctx, version, confirm, keep=1):
    """Put this release's pictures at the front, straight after the thumbnail.

    The gallery is what somebody sees before they read a word, and it was showing
    three releases of history first. `keep` is how many pictures at the front are left
    exactly where they are, because the first one is the thumbnail.
    """
    map_path = url_map_path(version)
    if not map_path.exists():
        print(f"no URL map at {map_path}; upload the images first")
        return 1
    theirs = set(json.loads(map_path.read_text(encoding="utf-8")).values())

    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    if not wait_for_human_signin(page, MEDIA_URL):
        print("not signed in")
        return 1
    page.goto(MEDIA_URL, wait_until="domcontentloaded")
    settle(page, 8)

    order = [item["url"] for item in page.evaluate(GALLERY_ITEMS_JS)]
    front = order[:keep]
    rest = order[keep:]
    # Counted from what is actually in the gallery, not from the map: the map also
    # holds pictures that live only in the description, and counting those made this
    # drag two of the old pictures it had no business touching.
    mine = [u for u in rest if u in theirs]
    wanted = front + mine + [u for u in rest if u not in theirs]
    moves_needed = sum(1 for a, b in zip(order, wanted) if a != b)
    print(f"{len(order)} pictures; {len(mine)} belong to {version}")
    print(f"{moves_needed} are not where they should be")
    if order == wanted:
        print("already in that order")
        return 0
    if not confirm:
        print("\ndry run. Re-run with --confirm to drag them into place.")
        return 0

    handles = page.locator('li button[aria-label="Drag and drop to reorder"]')
    # Measured, not assumed: a dropped picture lands at the FRONT of the gallery
    # wherever it is aimed. Aiming at slot 3 and at slot 5 both put it at 0. So the
    # order is built by dropping pictures on the front in reverse: the one that should
    # end up first is dropped last, and every drop is a move this grid actually makes.
    head = front + mine
    for url in reversed(head):
        current = [item["url"] for item in page.evaluate(GALLERY_ITEMS_JS)]
        source_index = current.index(url)
        if source_index == 0:
            continue
        if not drag_item(page, handles, source_index, 0):
            print("  could not reach a handle; stopping while the order is known")
            return 1
        landed = [item["url"] for item in page.evaluate(GALLERY_ITEMS_JS)].index(url)
        if landed != 0:
            print(f"  {url.rsplit('/', 1)[-1]} landed at {landed}, not the front. "
                  f"Stopping rather than shuffling blind.")
            return 1
        print(f"  front: {url.rsplit('/', 1)[-1]}")

    final = [item["url"] for item in page.evaluate(GALLERY_ITEMS_JS)]
    if final[:len(head)] != head:
        print("\nNOT in the wanted order; NOT saving")
        return 1
    # The order lives only in the page until this is pressed: a reload without it puts
    # every picture back where it was, which is what made the first attempt look like
    # the drags had done nothing.
    if not click_save(page):
        print("\nordered, but no enabled Save button was found. Press Save yourself.")
        return 1
    time.sleep(4)
    page.goto(MEDIA_URL, wait_until="domcontentloaded")
    settle(page, 8)
    saved = [item["url"] for item in page.evaluate(GALLERY_ITEMS_JS)]
    if saved[:len(head)] == head:
        print("\nsaved: the page reads back in the wanted order")
        return 0
    print("\nsaved, but the page reads back in a different order")
    return 1


def wanted_titles(page, version, also, fallback):
    """What every gallery picture should be called.

    Three sources, in order of how much they know: this release's URL map, any older
    folder of originals that can still be matched picture by picture, and finally a
    plain fallback for the ones nothing local can identify any more. A picture that
    ALREADY has a title is never touched: the thumbnail is named by hand and the
    fallback would happily overwrite it with a number.
    """
    titles = {}
    map_path = url_map_path(version)
    if map_path.exists():
        for stem, url in json.loads(map_path.read_text(encoding="utf-8")).items():
            titles[url] = f"{stem} {version}"

    for folder, old_version in (also or []):
        paths = sorted(Path(folder).expanduser().glob("*.png"))
        if not paths:
            print(f"  {folder}: no PNGs to match")
            continue
        print(f"  matching {len(paths)} original(s) from {folder} as {old_version}")
        found, _missing = match_gallery(page, [(path, path.stat().st_size) for path in paths])
        if found is None:
            return None
        for stem, url in found.items():
            titles.setdefault(url, f"{stem} {old_version}")

    items = page.evaluate(GALLERY_ITEMS_JS)
    spare = 0
    for item in items:
        if item["url"] in titles or item["title"] not in ("", "No title"):
            continue
        spare += 1
        titles[item["url"]] = f"{fallback} {spare}"
    return titles


def set_image_titles(ctx, version, confirm, also=None, fallback=None):
    """Name every gallery picture after the file it came from, plus the version.

    A Nexus image URL is an opaque number and the gallery ships every picture as
    "No title", so which shot is which lives only in a notes file that has to be kept
    by hand. Writing the name onto the picture itself puts it where it cannot drift.
    """
    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    if not wait_for_human_signin(page, MEDIA_URL):
        print("not signed in")
        return 1
    page.goto(MEDIA_URL, wait_until="domcontentloaded")
    settle(page, 8)

    titles = wanted_titles(page, version, also, fallback or "older")
    if titles is None:
        return 1
    items = page.evaluate(GALLERY_ITEMS_JS)
    planned = [(item, titles[item["url"]]) for item in items
               if item["url"] in titles and item["title"] != titles[item["url"]]]

    print(f"{len(items)} gallery picture(s), {len(planned)} to rename:")
    for item, wanted in planned:
        print(f"  {item['title'] or 'No title':28s} -> {wanted}")
    if not planned:
        return 0
    if not confirm:
        print("\ndry run. Re-run with --confirm to write these titles.")
        return 0

    renamed = 0
    for item, wanted in planned:
        try:
            # By id, not by position: renaming reflows nothing today, but an index
            # into a list that the page is free to reorder is how the wrong picture
            # gets the wrong name.
            page.locator(f'#{item["menuId"]}').click()
            time.sleep(0.6)
            page.get_by_role("menuitem", name="Edit title", exact=True).first.click()
            time.sleep(0.8)
            page.fill("input#media-title", wanted)
            time.sleep(0.3)
            page.locator('div.nxm-modal-footer button', has_text="Save").first.click()
            time.sleep(1.5)
            renamed += 1
            print(f"  named {wanted}")
        except Exception as exc:
            print(f"  FAILED on {wanted}: {exc}")
            try:
                page.keyboard.press("Escape")
            except Exception:
                pass

    settle(page, 4)
    after = {item["url"]: item["title"] for item in page.evaluate(GALLERY_ITEMS_JS)}
    wrong = [(item["url"], wanted) for item, wanted in planned if after.get(item["url"]) != wanted]
    print(f"\n{renamed} renamed; reading the page back, {len(planned) - len(wrong)} of "
          f"{len(planned)} carry the name they were given")
    for url, wanted in wrong:
        print(f"  still not {wanted!r}: {url.rsplit('/', 1)[-1]}")
    return 1 if wrong else 0


def upload_images(ctx, files, version, confirm, stage=False):
    page = ctx.pages[0] if ctx.pages else ctx.new_page()

    plan = []
    for name in files:
        path = Path(name).expanduser().resolve()
        if not path.exists():
            print(f"missing: {path}")
            return 1
        size = path.stat().st_size
        if size > IMAGE_SIZE_LIMIT:
            print(f"too big for Nexus ({size / 1e6:.1f} MB > 8 MB): {path}")
            return 1
        plan.append((path, size))

    print(f"{len(plan)} image(s) for the gallery at {MOD_URL}:")
    for path, size in plan:
        print(f"  {path.name:28s} {size / 1e6:5.2f} MB")
    if stage:
        return stage_images(ctx, plan, version)
    if not confirm:
        print("\ndry run. Nothing was uploaded.")
        print("  --stage    put them on the page as pending and let you press Save")
        print("  --confirm  upload and save them one at a time, no click needed")
        return 0

    if not wait_for_human_signin(page, MEDIA_URL):
        print("not signed in")
        return 1
    page.goto(MEDIA_URL, wait_until="domcontentloaded")
    settle(page)

    map_path = url_map_path(version)
    mapping = json.loads(map_path.read_text(encoding="utf-8")) if map_path.exists() else {}

    for index, (path, _size) in enumerate(plan, 1):
        before = gallery_urls(page)
        print(f"[{index}/{len(plan)}] {path.name} ... ", end="", flush=True)
        try:
            page.set_input_files('input[type=file][accept*="image/png"]', str(path))
        except Exception as exc:
            print(f"FAILED to hand the file over: {exc}")
            return 1
        time.sleep(2)
        if not click_save(page):
            print("FAILED: no enabled Save button appeared")
            return 1

        # One file at a time, and the URL is whichever one is new. Uploading a batch
        # would be faster but nothing in the page ties a returned URL back to the file
        # it came from, and a gallery row filed against the wrong picture is worse than
        # a slow upload.
        found = None
        deadline = time.time() + 180
        while time.time() < deadline:
            time.sleep(2)
            fresh = gallery_urls(page) - before
            if len(fresh) == 1:
                found = fresh.pop()
                break
            if len(fresh) > 1:
                print(f"FAILED: {len(fresh)} new URLs appeared at once, cannot tell them apart")
                return 1
        if not found:
            print("FAILED: no new URL appeared within 3 minutes")
            return 1
        mapping[path.stem] = found
        map_path.parent.mkdir(parents=True, exist_ok=True)
        map_path.write_text(json.dumps(mapping, indent=2, ensure_ascii=False), encoding="utf-8")
        print(found.rsplit("/", 1)[-1])

    print(f"\n{len(plan)} uploaded. Map written to {map_path}")
    append_index_rows(mapping, [p.name for p, _ in plan], version)
    return 0


def append_index_rows(mapping, filenames, version):
    """Record which URL is which picture, because a Nexus URL is an opaque number."""
    stems = [Path(n).stem for n in filenames]
    rows = [f"| `final/{stem}.png` | {version} gallery | `{mapping[stem]}` |"
            for stem in stems if stem in mapping]
    if not rows:
        return
    block = ["", f"## Uploaded {time.strftime('%d.%m.%Y')}, the {version} gallery", "",
             f"Shot by `tools/shotgallery.py` at 1920x1080, chosen by eye, in "
             f"`~/Documents/Radiance-Shots-{version}/final/`. Each picture also carries its "
             f"own name on Nexus, `<file> {version}`, so this table is a convenience rather "
             f"than the only record.", "",
             "| Local file | Where used | Nexus URL |", "|---|---|---|", *rows, ""]
    with INDEX_FILE.open("a", encoding="utf-8") as handle:
        handle.write("\n".join(block))
    print(f"appended {len(rows)} row(s) to {INDEX_FILE}")


# ---------- description ----------

# The description field is SCEditor: a toolbar, an iframe for the rich view, a source
# textarea inside the container, and the original textarea holding the BBCODE.
FIND_EDITOR_JS = """
() => {
  const all = [...document.querySelectorAll('textarea')];
  const original = all.find(t => !t.closest('.sceditor-container')
                                 && (t.placeholder || '').startsWith('Describe your mod'));
  return original ? original.value : null;
}
"""

SET_EDITOR_JS = """
(text) => {
  const all = [...document.querySelectorAll('textarea')];
  const original = all.find(t => !t.closest('.sceditor-container')
                                 && (t.placeholder || '').startsWith('Describe your mod'));
  if (!original) return 'no editor textarea found';
  // Drive the editor through its own API where it exists, so the rich view and the
  // backing field cannot disagree about what the description says.
  let viaApi = false;
  try {
    const instance = window.sceditor && window.sceditor.instance
      ? window.sceditor.instance(original) : null;
    if (instance) { instance.val(text); viaApi = true; }
  } catch (e) { /* fall through to the field itself */ }
  // React owns the value property, so a plain assignment is swallowed without a trace.
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLTextAreaElement.prototype, 'value').set;
  setter.call(original, text);
  original.dispatchEvent(new Event('input', { bubbles: true }));
  original.dispatchEvent(new Event('change', { bubbles: true }));
  return (original.value === text ? 'ok' : 'value did not stick')
    + (viaApi ? ' (sceditor api)' : ' (field only)');
}
"""


def read_draft(path):
    """Pull the BBCODE out of the fenced block in the draft note."""
    text = Path(path).read_text(encoding="utf-8")
    blocks, inside, current = [], False, []
    for line in text.splitlines():
        if line.startswith("```"):
            if inside:
                blocks.append("\n".join(current))
                current = []
            inside = not inside
            continue
        if inside:
            current.append(line)
    real = [b for b in blocks if "[center]" in b or "[size=" in b]
    if len(real) != 1:
        raise SystemExit(f"expected exactly one BBCODE block in {path}, found {len(real)}")
    return real[0]


def fill_slots(body, version):
    """Swap every PASTE_URL_* for a real URL, or refuse to go near the page."""
    slots = sorted(set(re.findall(r"PASTE_URL_[A-Za-z0-9_-]+", body)))
    if not slots:
        return body, []
    path = url_map_path(version)
    mapping = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    unresolved = []
    for slot in slots:
        stem = slot[len("PASTE_URL_"):]
        if stem in mapping:
            body = body.replace(slot, mapping[stem])
        else:
            unresolved.append(slot)
    return body, unresolved


def publish_description(ctx, draft, version, confirm, stage=False):
    body = read_draft(draft)
    body, unresolved = fill_slots(body, version)
    if unresolved:
        print(f"{len(unresolved)} image slot(s) still have no URL: {', '.join(unresolved)}")
        print(f"upload those first; the map lives at {url_map_path(version)}")
        return 1

    page = ctx.pages[0] if ctx.pages else ctx.new_page()
    if not wait_for_human_signin(page, GENERAL_URL):
        print("not signed in")
        return 1
    page.goto(GENERAL_URL, wait_until="domcontentloaded")
    settle(page, 8)

    live = page.evaluate(FIND_EDITOR_JS)
    if live is None:
        print("could not find the description editor on the page")
        return 1

    print(f"live page:  {len(live):,} characters")
    print(f"new draft:  {len(body):,} characters")
    same = sum(1 for a, b in zip(live.split("\n"), body.split("\n")) if a == b)
    print(f"lines: {len(live.splitlines())} live, {len(body.splitlines())} new, "
          f"{same} identical from the top")
    scratch = PROBE_DIR / "description-live.local.txt"
    scratch.parent.mkdir(parents=True, exist_ok=True)
    scratch.write_text(live, encoding="utf-8")
    print(f"the live text is saved at {scratch} so it can be diffed or restored")

    if not (confirm or stage):
        print("\ndry run. The page was not touched.")
        print("  --stage    type it into the editor and leave the Save click to you")
        print("  --confirm  type it in and save it")
        return 0

    # Through the editor's own source view, the way it is done by hand: the toolbar's
    # [ ] button swaps the rich view for the raw BBCODE, and filling THAT is what the
    # author can then read back on screen. Writing the hidden field instead would set
    # a value nobody can see, and a staged change nobody can see is worse than none.
    source_button = page.locator("a.sceditor-button-source")
    in_source_view = False
    if source_button.count():
        source_button.first.click()
        time.sleep(1.5)
        source_box = page.locator(".sceditor-container textarea").first
        if source_box.count() and source_box.is_visible():
            source_box.fill(body)
            time.sleep(0.8)
            in_source_view = True

    result = page.evaluate(SET_EDITOR_JS, body)
    print(f"editor says: {result}"
          + (" (typed into the source view)" if in_source_view else " (source view not offered)"))
    if not result.startswith("ok"):
        return 1

    if stage:
        # The description replaces public text, so this is the one that stops here by
        # default: read the real page, in the real editor, then press Save yourself.
        print("\nThe new description is IN THE EDITOR and NOT saved. Nothing is public yet.")
        print("Read it in the Edge window, then press Save there yourself.")
        print("Leaving the browser open for an hour; close it or stop me when you are done.")
        deadline = time.time() + 3600
        while time.time() < deadline:
            time.sleep(10)
            try:
                if page.is_closed():
                    break
            except Exception:
                break
        return 0

    if not click_save(page):
        print("no enabled Save button found")
        return 1
    time.sleep(6)

    # A save that reports success and changes nothing is the failure this checks for.
    page.goto(GENERAL_URL, wait_until="domcontentloaded")
    settle(page, 8)
    after = page.evaluate(FIND_EDITOR_JS) or ""
    if after.strip() == body.strip():
        print("verified: the live page now reads back exactly what was written")
        return 0
    print(f"NOT saved: the page reads back {len(after):,} characters, not {len(body):,}")
    return 1


# ---------- entry ----------

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    # Which release is being published. Defaults to manifest.json so the map file,
    # the index rows and the picture titles cannot drift apart, or be left at last
    # release's number by a tool nobody remembered to edit.
    versioned = argparse.ArgumentParser(add_help=False)
    versioned.add_argument("--version", default=None,
                           help="release these belong to (default: manifest.json)")

    session = sub.add_parser("session", help="inspect or back up the browser sign-in")
    session.add_argument("action", choices=["check", "save", "restore"])

    sub.add_parser("probe", help="describe the edit pages; writes nothing to Nexus")

    match = sub.add_parser("match", parents=[versioned],
                           help="which of these files are on the page already")
    match.add_argument("files", nargs="+")

    titles = sub.add_parser("titles", parents=[versioned],
                            help="name each gallery picture after its file")
    titles.add_argument("--also", action="append", metavar="DIR=VERSION", default=[],
                        help="an older folder of originals and the release they "
                             "shipped in; repeatable")
    titles.add_argument("--fallback", default="older",
                        help="what to call a picture nothing local can identify")
    titles.add_argument("--confirm", action="store_true")

    reorder = sub.add_parser("reorder", parents=[versioned],
                             help="put this release's pictures first")
    reorder.add_argument("--keep", type=int, default=1,
                         help="how many at the front to leave alone (the thumbnail)")
    reorder.add_argument("--confirm", action="store_true")

    upload = sub.add_parser("upload", parents=[versioned],
                            help="put images in the mod's gallery")
    upload.add_argument("files", nargs="+")
    upload.add_argument("--stage", action="store_true",
                        help="leave them pending and let you press Save")
    upload.add_argument("--confirm", action="store_true",
                        help="upload and save without asking")

    description = sub.add_parser("description", parents=[versioned],
                                 help="replace the mod's description")
    description.add_argument("draft", help="the .md note holding the BBCODE block")
    description.add_argument("--stage", action="store_true",
                             help="type it in and let you press Save")
    description.add_argument("--confirm", action="store_true",
                             help="type it in and save it")

    args = parser.parse_args()
    version = getattr(args, "version", None) or (
        mod_version() if hasattr(args, "version") else None)
    if version:
        print(f"working against version {version}")

    with sync_playwright() as playwright:
        ctx = launch(playwright)
        try:
            if args.command == "session":
                page = ctx.pages[0] if ctx.pages else ctx.new_page()
                if args.action == "restore":
                    restore_session(ctx)
                ok = wait_for_human_signin(page, MOD_URL)
                print(f"signed in: {ok}")
                if args.action == "save":
                    if not ok:
                        print("refusing to save a signed-out session")
                        return 1
                    save_session(ctx)
                return 0 if ok else 1
            if args.command == "probe":
                return probe(ctx)
            if args.command == "match":
                return match_only(ctx, args.files, version)
            if args.command == "titles":
                also = []
                for pair in args.also:
                    if "=" not in pair:
                        print(f"--also wants DIR=VERSION, got {pair!r}")
                        return 1
                    folder, _, old = pair.rpartition("=")
                    also.append((folder, old))
                return set_image_titles(ctx, version, args.confirm, also, args.fallback)
            if args.command == "reorder":
                return reorder_gallery(ctx, version, args.confirm, args.keep)
            if args.command == "upload":
                return upload_images(ctx, args.files, version, args.confirm, args.stage)
            if args.command == "description":
                return publish_description(ctx, args.draft, version, args.confirm, args.stage)
        finally:
            # Close, never kill: Chromium flushes its cookie store on a clean exit and
            # a killed browser is how a sign-in gets lost.
            ctx.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
