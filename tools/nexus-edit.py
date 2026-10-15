#!/usr/bin/env python
"""Read and edit the mod author's own comments on the SDV-Radiance Nexus page (mods/49397): comments
on the Posts tab and replies on the Bugs tab. Same logged-in Edge profile and helpers as
tools/nexus-reply.py; the Nexus API cannot edit comments.

    python tools/nexus-edit.py read out.json                 # BBCODE source of every comment of ours
    python tools/nexus-edit.py apply plan.json --dry-run     # fill every edit form, submit nothing
    python tools/nexus-edit.py apply plan.json --only 177225402
    python tools/nexus-edit.py apply plan.json               # everything not yet in the ledger

read: the edit form's textarea already holds the comment's BBCODE source, so nothing is clicked.
Which comments are ours comes from the inbox fetch (docs/local/nexus/inbox/comments.json, author
PHUICMT), so run tools/nexus-inbox.py fetch first. The output maps "post:<id>" and
"bug<issue>:<reply id>" to {type, id, bug, source}.

apply: the plan is a JSON list of {type: "post"|"bugreply", id, bug (bugreply only), who, text}.
Text rules are the reply poster's (under 5,000 characters, no em dash, no emoji on a line with a
tag, no 4-byte emoji in a bug reply). The form is filled in BBCODE mode, read back, submitted, and
the run waits until the comment on the page shows the new text. Every edit is appended to
docs/local/nexus/replies/edited.local.jsonl with a hash of its text, and the same text to the same
comment is never sent twice, so a run that dies can be started again.

Controls (probed 2026-10-12):
  Posts: a#edit-comment-{id} toggles div#edit-comment-form-{id}; textarea#edit-comment-post-{id};
         a#submit-edit-comment-{id}; the shown text is div#comment-content-{id}.
  Bugs:  after loadIssueReplies(bug), "Edit post" toggles div#edit-bug-reply-form-{id};
         textarea#edit-bug-reply-post-{id}; a#submit-edit-bug-reply-form-{id}; the reply is
         li#bug-reply-tile-{id}.
"""
import argparse
import importlib.util
import json
import re
import time
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LEDGER = REPO / "docs" / "local" / "nexus" / "replies" / "edited.local.jsonl"
COMMENTS = REPO / "docs" / "local" / "nexus" / "inbox" / "comments.json"

spec = importlib.util.spec_from_file_location("nexus_reply", REPO / "tools" / "nexus-reply.py")
reply = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reply)
log, text_hash, check_text, fill_wysibb, same_text, shot = (
    reply.log, reply.text_hash, reply.check_text, reply.fill_wysibb, reply.same_text, reply.shot)


def own_comments():
    """(post ids, {bug id: [reply ids]}) written by the author, from the last inbox fetch."""
    data = json.loads(COMMENTS.read_text(encoding="utf-8"))
    post_ids, bug_replies = [], {}

    def walk(items, bug=None):
        for item in items:
            if (item.get("author") or "").lower() == "phuicmt":
                raw_id = str(item.get("id"))
                if bug is None:
                    post_ids.append(raw_id)
                elif raw_id.startswith("bug-"):
                    bug_replies.setdefault(bug, []).append(raw_id.replace("bug-", ""))
            if isinstance(item.get("replies"), list):
                walk(item["replies"], bug)

    walk(data.get("posts", []))
    for bug in data.get("bugs", []):
        if isinstance(bug.get("comments"), list):
            walk(bug["comments"], str(bug.get("id")))
    return post_ids, bug_replies


def plain(bbcode):
    """A few letters of a text with the tags and all whitespace taken out, to look for on the page
    after a submit. Whitespace goes too because a heading in [b] followed by a blank line shows
    as "usedHi" in the page text, which failed a real edit on 2026-10-12."""
    letters = re.sub(r"\[[^\]]*\]", "", bbcode)
    letters = re.sub(r"\s+", "", letters)
    return letters[:40]


def read_sources(page, out_path):
    post_ids, bug_replies = own_comments()
    sources = {}
    page_index = reply.posts_page_index(page)
    by_page = {}
    for comment_id in post_ids:
        if comment_id in page_index:
            by_page.setdefault(page_index[comment_id], []).append(comment_id)
        else:
            log(f"  post {comment_id} is not on any page (deleted?)")
    for number in sorted(by_page):
        reply.goto_posts_page(page, number)
        for comment_id in by_page[number]:
            source = page.evaluate(f"() => {{ const t = document.querySelector('#edit-comment-post-{comment_id}'); return t ? t.value : null; }}")
            if source is None:
                log(f"  post {comment_id}: no edit form on page {number}")
                continue
            sources[f"post:{comment_id}"] = {"type": "post", "id": comment_id, "source": source}
        log(f"posts page {number}: {len(by_page[number])} of ours")
    bug_index = reply.bugs_page_index(page)
    for bug, reply_ids in bug_replies.items():
        try:
            reply.open_bug(page, bug, bug_index)
        except Exception as error:
            log(f"  bug {bug}: {error}")
            continue
        for reply_id in reply_ids:
            source = page.evaluate(f"() => {{ const t = document.querySelector('#edit-bug-reply-post-{reply_id}'); return t ? t.value : null; }}")
            if source is None:
                log(f"  bug {bug} reply {reply_id}: no edit form")
                continue
            sources[f"bug{bug}:{reply_id}"] = {"type": "bugreply", "id": reply_id, "bug": bug, "source": source}
        log(f"bug {bug}: {len(reply_ids)} of ours")
        time.sleep(2)
    Path(out_path).write_text(json.dumps(sources, ensure_ascii=False, indent=1), encoding="utf-8")
    log(f"wrote {len(sources)} sources to {out_path}")


def edit_post(page, item, page_index, dry_run):
    comment_id = str(item["id"])
    number = page_index.get(comment_id)
    if number is None:
        raise RuntimeError(f"comment {comment_id} is not on any Posts page")
    reply.goto_posts_page(page, number)
    page.evaluate(f"() => document.querySelector('#comment-{comment_id}').scrollIntoView({{block: 'center'}})")
    # The Edit link builds the editor; showing the form by hand leaves a bare textarea with no
    # toolbar, and the source-view switch is never there to click (dry run, 2026-10-12).
    if not page.locator(f"#edit-comment-form-{comment_id}").first.is_visible():
        page.evaluate(f"() => document.querySelector('#edit-comment-{comment_id}').click()")
    page.wait_for_selector(f"#edit-comment-form-{comment_id} .mswitch", state="visible", timeout=15000)
    held = fill_wysibb(page, f"#edit-comment-form-{comment_id}", f"edit-comment-post-{comment_id}", item["text"])
    if not same_text(held, item["text"]):
        shot(page, f"edit-{comment_id}-mismatch")
        raise RuntimeError(f"editor holds different text for {comment_id}:\n{held[:300]}")
    if dry_run:
        shot(page, f"edit-{comment_id}-dry")
        page.evaluate(f"() => $('#edit-comment-form-{comment_id}').hide()")
        return {"dry": True}
    page.locator(f"#submit-edit-comment-{comment_id}").first.click()
    snippet = plain(item["text"])
    try:
        page.wait_for_function(
            "([id, snippet]) => { const c = document.querySelector('#comment-content-' + id); return c && c.textContent.replace(/\\s+/g, '').includes(snippet); }",
            arg=[comment_id, snippet], timeout=30000)
    except reply.PWTimeout:
        error = page.evaluate(f"() => (document.querySelector('#edit-comment-error-message-{comment_id}') || {{}}).textContent || ''")
        shot(page, f"edit-{comment_id}-unchanged")
        raise RuntimeError(f"comment {comment_id} did not show the new text; error: {error.strip()[:200]}")
    return {"edited": True}


def edit_bug_reply(page, item, bug_index, dry_run):
    reply_id, bug = str(item["id"]), str(item["bug"])
    reply.open_bug(page, bug, bug_index)
    page.evaluate(f"() => $('#edit-bug-reply-form-{reply_id}').show()")
    page.wait_for_selector(f"#edit-bug-reply-form-{reply_id}", state="visible", timeout=10000)
    held = fill_wysibb(page, f"#edit-bug-reply-form-{reply_id}", f"edit-bug-reply-post-{reply_id}", item["text"])
    if not same_text(held, item["text"]):
        shot(page, f"editbug-{reply_id}-mismatch")
        raise RuntimeError(f"editor holds different text for bug reply {reply_id}:\n{held[:300]}")
    if dry_run:
        shot(page, f"editbug-{reply_id}-dry")
        page.evaluate(f"() => $('#edit-bug-reply-form-{reply_id}').hide()")
        return {"dry": True}
    page.locator(f"#submit-edit-bug-reply-form-{reply_id}").first.click()
    snippet = plain(item["text"])
    try:
        page.wait_for_function(
            "([id, snippet]) => { const c = document.querySelector('#bug-reply-tile-' + id); if (!c) return false; const shown = [...c.querySelectorAll('*')].filter(e => !e.closest('[id^=edit-bug-reply-form]')).map(e => e.childNodes.length ? [...e.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join('') : '').join('').replace(/\\s+/g, ''); return shown.includes(snippet); }",
            arg=[reply_id, snippet], timeout=30000)
    except reply.PWTimeout:
        error = page.evaluate(f"() => (document.querySelector('#edit-bug-reply-error-message-{reply_id}') || {{}}).textContent || ''")
        shot(page, f"editbug-{reply_id}-unchanged")
        raise RuntimeError(f"bug reply {reply_id} did not show the new text; error: {error.strip()[:200]}")
    return {"edited": True}


def edited_before():
    done = set()
    if LEDGER.exists():
        for line in LEDGER.read_text(encoding="utf-8").splitlines():
            if line.strip():
                entry = json.loads(line)
                if entry.get("ok"):
                    done.add((entry["type"], str(entry["id"]), entry["text_hash"]))
    return done


def record(entry):
    LEDGER.parent.mkdir(parents=True, exist_ok=True)
    with LEDGER.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry, ensure_ascii=False) + "\n")


def apply_plan(page, plan_path, dry_run, only, delay, limit):
    plan = json.loads(Path(plan_path).read_text(encoding="utf-8"))
    for item in plan:
        check_text({**item, "type": "bug" if item["type"] == "bugreply" else item["type"]})
    done = edited_before()
    todo = [item for item in plan if (item["type"], str(item["id"]), text_hash(item["text"])) not in done]
    if only:
        todo = [item for item in todo if str(item["id"]) in only]
    if limit:
        todo = todo[:limit]
    log(f"{len(plan)} in plan, {len(todo)} to do" + (" (DRY RUN)" if dry_run else ""))
    page_index = bug_index = None
    failures = 0
    for number, item in enumerate(todo, 1):
        label = f"{item['type']} {item['id']} ({item.get('who', '')})"
        reply.inbox.progress(page, f"edit {number}/{len(todo)}: {label}")
        try:
            if item["type"] == "post":
                if page_index is None:
                    page_index = reply.posts_page_index(page)
                result = edit_post(page, item, page_index, dry_run)
                bug_index = None
            else:
                if bug_index is None:
                    bug_index = reply.bugs_page_index(page)
                result = edit_bug_reply(page, item, bug_index, dry_run)
                page_index = None
                bug_index = None    # a fresh Bugs tab each time, as the reply poster learned
            log(f"  ok {label}: {result}")
            if not dry_run:
                record({"type": item["type"], "id": str(item["id"]), "who": item.get("who", ""), "ok": True,
                        "when": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                        "text_hash": text_hash(item["text"])})
        except Exception as error:
            failures += 1
            log(f"  FAILED {label}: {error}")
            if not dry_run:
                record({"type": item["type"], "id": str(item["id"]), "who": item.get("who", ""), "ok": False,
                        "when": datetime.now(timezone.utc).isoformat(timespec="seconds"), "error": str(error)[:500]})
            if failures >= 3:
                log("three failures, stopping")
                break
            page_index = bug_index = None
        if number < len(todo) and not dry_run:
            time.sleep(delay)
    log("done" + (f", {failures} failure(s)" if failures else ""))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["read", "apply"])
    parser.add_argument("path")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--only", action="append", default=[])
    parser.add_argument("--delay", type=float, default=20.0)
    parser.add_argument("--limit", type=int, default=0)
    arguments = parser.parse_args()
    with reply.sync_playwright() as playwright:
        context = reply.inbox.launch(playwright, True)
        page = context.pages[0] if context.pages else context.new_page()
        try:
            if arguments.mode == "read":
                read_sources(page, arguments.path)
            else:
                apply_plan(page, arguments.path, arguments.dry_run, arguments.only, arguments.delay, arguments.limit)
        finally:
            context.close()


if __name__ == "__main__":
    main()
