#!/usr/bin/env python3
"""Keep the kanban board consistent: merged tasks, blocked state, invariants and BOARD.md.

Subcommands:
  sync              everything below in order: record merged task branches as done, fix blocked state,
                    regenerate BOARD.md, report invariant violations. Run it on `main` after any change.
  merged            record task branches merged into main (merge commit or merged GitHub PR) as `done`
  unblock           blocked → backlog when every blocker is done; backlog → blocked when one isn't
  check             report invariant violations (kanban/README.md → Invariants); exit 1 if any
  board             regenerate kanban/BOARD.md (rewritten only when something besides the timestamp changed)
  blockers T-###    scan for tasks a new (or edited) task is probably blocked by, and tasks it probably blocks
  unblocks T-###    list the tasks that wait on T-### and which of them become ready once it is done

Changed tasks are reported on stderr as `done: T-###`, `unblocked: T-###` and `blocked: T-###`.
`merged` asks GitHub (`gh pr list`) for merged PRs when `gh` works, so squash and rebase merges count too;
without `gh` it only sees merge commits ("Merge pull request #N from <owner>/task/T-###", "Merge T-###: …").
"""
import argparse
import json
import re
import subprocess
import sys
from datetime import datetime
from pathlib import Path

KANBAN_DIR = Path(__file__).resolve().parent.parent
TASKS_DIR = KANBAN_DIR / "tasks"
BOARD_PATH = KANBAN_DIR / "BOARD.md"
REPO_DIR = KANBAN_DIR.parent
STATUSES = ("backlog", "in-progress", "blocked", "review", "done")
PRIORITY_ORDER = {"P0": 0, "P1": 1, "P2": 2, "P3": 3}
TASK_BRANCH = re.compile(r"^task/(T-\d+)$")
MERGE_SUBJECTS = (
    re.compile(r"^Merge pull request #(?P<pr>\d+) from \S+?/task/(?P<id>T-\d+)$"),
    re.compile(r"^Merge (?P<id>T-\d+): "),
    re.compile(r"^Merge branch 'task/(?P<id>T-\d+)'(?: into main)?$"),
)


# ---------------------------------------------------------------- task files

def read_front_matter(text):
    match = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    if not match:
        return {}
    fm = {}
    for line in match.group(1).splitlines():
        key, sep, value = line.partition(":")
        if sep and not line.startswith((" ", "\t")):
            fm[key.strip()] = re.sub(r"\s+#.*$", "", value).strip().strip('"')
    return fm


def parse_id_list(value):
    return re.findall(r"T-\d+", value or "")


def parse_str_list(value):
    return re.findall(r'"([^"]*)"', value or "") or [v.strip() for v in (value or "").strip("[]").split(",") if v.strip()]


def load_tasks():
    tasks = {}
    for path in sorted(TASKS_DIR.glob("T-*.md")):
        text = path.read_text(encoding="utf-8")
        fm = read_front_matter(text)
        if "id" not in fm:
            print(f"warning: {path.name} has no id in its front matter", file=sys.stderr)
            continue
        fm["blocked_by"] = parse_id_list(fm.get("blocked_by"))
        fm["conflicts_with"] = parse_id_list(fm.get("conflicts_with"))
        fm["area"] = parse_str_list(fm.get("area"))
        if fm["id"] in tasks:
            fm["duplicate_of"] = tasks[fm["id"]]["path"].name
        tasks[fm["id"]] = {"path": path, "text": text, **fm}
    return tasks


def set_fields(task, **fields):
    head, sep, body = task["text"].partition("\n---\n")
    for key, value in fields.items():
        line = f"{key}: {value}"
        head, n = re.subn(rf"^{re.escape(key)}:.*$", line.replace("\\", r"\\"), head, count=1, flags=re.M)
        if not n:
            head += "\n" + line
        task[key] = str(value).strip('"')
    task["text"] = head + sep + body


def append_log(task, line):
    task["text"] = task["text"].rstrip("\n") + "\n" + line + "\n"


def append_review(task, line):
    match = re.search(r"(## Review\n.*?)(\n+)(## Activity log)", task["text"], re.S)
    if match:
        task["text"] = (task["text"][:match.start()] + match.group(1).rstrip("\n") + "\n\n" + line + "\n\n"
                        + match.group(3) + task["text"][match.end():])


def save(task):
    task["path"].write_text(task["text"], encoding="utf-8")


def log_entries(task):
    return re.findall(r"^- (\d{4}-\d{2}-\d{2} \d{2}:\d{2}) · ([^·]*?) · (.*)$", task["text"], re.M)


def last_move_to(task, status):
    for when, _actor, rest in reversed(log_entries(task)):
        if re.search(rf"→ {re.escape(status)}\b", rest.split(" · ")[0]):
            return when
    return ""


def not_done(task, tasks):
    return [b for b in task["blocked_by"] if tasks.get(b, {}).get("status") != "done"]


def blockers_done(task, tasks):
    for blocker in task["blocked_by"]:
        if blocker not in tasks:
            print(f"warning: {task['id']} is blocked by unknown task {blocker}", file=sys.stderr)
            return False
    return not not_done(task, tasks)


def sort_key(task):
    return PRIORITY_ORDER.get(task.get("priority"), 9), task["id"]


# ---------------------------------------------------------------- merged → done

def git(*args):
    return subprocess.run(["git", "-C", str(REPO_DIR), *args], capture_output=True, text=True, check=True).stdout


def merged_from_git(ref):
    merged = {}
    try:
        log = git("log", ref, "--first-parent", "--merges", "--format=%h%x09%s")
    except subprocess.CalledProcessError as error:
        print(f"warning: git log {ref} failed: {error.stderr.strip()}", file=sys.stderr)
        return merged
    for line in reversed(log.splitlines()):
        commit, _, subject = line.partition("\t")
        for pattern in MERGE_SUBJECTS:
            match = pattern.match(subject)
            if match:
                pr = match.groupdict().get("pr")
                merged.setdefault(match["id"], {"commit": commit, "pr": pr})
                break
    return merged


def merged_from_github():
    try:
        out = subprocess.run(["gh", "pr", "list", "--state", "merged", "--base", "main", "--limit", "500",
                              "--json", "number,headRefName,mergeCommit"],
                             cwd=REPO_DIR, capture_output=True, text=True, check=True, timeout=60).stdout
    except (OSError, subprocess.SubprocessError) as error:
        detail = getattr(error, "stderr", "") or error
        print(f"note: GitHub PR lookup skipped ({str(detail).strip()[:120]}); using merge commits only",
              file=sys.stderr)
        return {}
    merged = {}
    for pr in sorted(json.loads(out), key=lambda p: p["number"]):
        match = TASK_BRANCH.match(pr.get("headRefName", ""))
        if match:
            commit = ((pr.get("mergeCommit") or {}).get("oid") or "")[:7]
            merged.setdefault(match[1], {"commit": commit, "pr": str(pr["number"])})
    return merged


def cmd_merged(tasks, now, ref="main", github=True):
    merged = merged_from_github() if github else {}
    for task_id, info in merged_from_git(ref).items():
        merged.setdefault(task_id, info)
    changed = []
    for task_id, info in sorted(merged.items()):
        task = tasks.get(task_id)
        if not task or task.get("status") == "done":
            continue
        source = f"PR #{info['pr']} merged into main" if info["pr"] else "task branch merged into main"
        at = f" @ {info['commit']}" if info["commit"] else ""
        old = task.get("status")
        fields = {"status": "done", "updated": f"{now:%Y-%m-%d}"}
        if not task.get("branch"):
            fields["branch"] = f'"task/{task_id}"'
        set_fields(task, **fields)
        append_review(task, f"- {now:%Y-%m-%d %H:%M} · approve · {source}{at} (recorded by kanban-sync)")
        append_log(task, f"- {now:%Y-%m-%d %H:%M} · user · {old} → done · {source}{at} (synced by kanban-sync)")
        save(task)
        changed.append(task_id)
        print(f"done: {task_id} ({source}{at}, was {old})", file=sys.stderr)
    return changed


# ---------------------------------------------------------------- blocked state

def unblock(task, now):
    set_fields(task, status="backlog", updated=f"{now:%Y-%m-%d}")
    append_log(task, f"- {now:%Y-%m-%d %H:%M} · kanban-board · blocked → backlog · "
                     f"blockers done: {', '.join(task['blocked_by'])}")
    save(task)


def block(task, tasks, now):
    set_fields(task, status="blocked", updated=f"{now:%Y-%m-%d}")
    append_log(task, f"- {now:%Y-%m-%d %H:%M} · kanban-board · backlog → blocked · "
                     f"waiting on: {', '.join(not_done(task, tasks))}")
    save(task)


def cmd_unblock(tasks, now, reblock=True):
    """Fix the blocked state in both directions. In-progress tasks are only warned about."""
    unblocked, blocked = [], []
    for task in sorted(tasks.values(), key=lambda t: t["id"]):
        status = task.get("status")
        if status == "blocked" and task["blocked_by"] and blockers_done(task, tasks):
            unblock(task, now)
            unblocked.append(task["id"])
            print(f"unblocked: {task['id']} (blockers done: {', '.join(task['blocked_by'])})", file=sys.stderr)
        elif reblock and status == "backlog" and not_done(task, tasks):
            waiting = not_done(task, tasks)
            block(task, tasks, now)
            blocked.append(task["id"])
            print(f"blocked: {task['id']} (waiting on: {', '.join(waiting)})", file=sys.stderr)
    return unblocked, blocked


# ---------------------------------------------------------------- invariants

def glob_prefix(pattern):
    return re.split(r"[*?\[{]", pattern, maxsplit=1)[0]


def globs_overlap(a, b):
    pa, pb = glob_prefix(a), glob_prefix(b)
    if pa == a and pb == b:
        return a == b
    if pa == a:
        return a.startswith(pb) and re.fullmatch(glob_to_regex(b), a) is not None
    if pb == b:
        return b.startswith(pa) and re.fullmatch(glob_to_regex(a), b) is not None
    return pa.startswith(pb) or pb.startswith(pa)


def glob_to_regex(pattern):
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        elif pattern[i] == "?":
            out, i = out + "[^/]", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return out


def areas_overlap(a, b):
    return sorted({f"{x} ~ {y}" for x in a.get("area", []) for y in b.get("area", []) if globs_overlap(x, y)})


def reachable(start, edges):
    seen, stack = set(), list(edges.get(start, []))
    while stack:
        node = stack.pop()
        if node not in seen:
            seen.add(node)
            stack.extend(edges.get(node, []))
    return seen


def ancestors(task_id, tasks):
    return reachable(task_id, {t: tasks[t]["blocked_by"] for t in tasks})


def descendants(task_id, tasks):
    children = {}
    for t in tasks.values():
        for b in t["blocked_by"]:
            children.setdefault(b, []).append(t["id"])
    return reachable(task_id, children)


def cmd_check(tasks):
    problems = []

    def bad(task_id, rule, msg):
        problems.append(f"{task_id}: invariant {rule}: {msg}")

    in_progress = [t for t in tasks.values() if t.get("status") == "in-progress"]
    for task_id, t in sorted(tasks.items()):
        status = t.get("status")
        if status not in STATUSES:
            bad(task_id, 1, f"unknown status `{status}`")
        missing = not_done(t, tasks)
        if status in ("backlog", "in-progress") and missing:
            bad(task_id, 2, f"{status} but blocked by unfinished {', '.join(missing)}")
        if status == "blocked" and not missing:
            bad(task_id, 3, "blocked but no unfinished blocker" if t["blocked_by"] else "blocked with empty blocked_by")
        if status in ("in-progress", "review") and not t.get("owner"):
            bad(task_id, 4, f"{status} without owner")
        if status in ("backlog", "blocked") and t.get("owner"):
            bad(task_id, 4, f"{status} with owner `{t['owner']}`")
        for b in t["blocked_by"]:
            if b not in tasks:
                bad(task_id, 5, f"blocked_by names unknown task {b}")
        if task_id in t["blocked_by"]:
            bad(task_id, 5, "blocks itself")
        elif task_id in ancestors(task_id, tasks):
            bad(task_id, 5, "dependency cycle")
        if status == "done":
            entries = log_entries(t)
            if not entries or "→ done" not in entries[-1][2]:
                bad(task_id, 6, "done but the last Activity log entry isn't the approval/merge")
        if not t["path"].name.startswith(f"{task_id}-"):
            bad(task_id, 7, f"file name {t['path'].name} doesn't match id")
        if t.get("duplicate_of"):
            bad(task_id, 7, f"id reused by {t['duplicate_of']}")
        parallel = t.get("parallel")
        if parallel not in ("yes", "no"):
            bad(task_id, 9, f"parallel is `{parallel}`")
        elif parallel == "no" and not t["conflicts_with"]:
            bad(task_id, 9, "parallel: no with empty conflicts_with")
        elif parallel == "yes" and t["conflicts_with"]:
            bad(task_id, 9, "parallel: yes with non-empty conflicts_with")
        related = ancestors(task_id, tasks) | descendants(task_id, tasks)
        for c in t["conflicts_with"]:
            if c not in tasks:
                bad(task_id, 9, f"conflicts_with names unknown task {c}")
            elif status != "done" and task_id not in tasks[c]["conflicts_with"]:
                bad(task_id, 9, f"conflicts_with {c} isn't symmetric")
            elif status != "done" and c in related:
                bad(task_id, 9, f"conflicts_with {c}, which is its own blocker or dependant")
        if status in ("in-progress", "review") and not t.get("taken_by"):
            bad(task_id, 11, f"{status} without taken_by")
    for i, a in enumerate(in_progress):
        for b in in_progress[i + 1:]:
            overlap = areas_overlap(a, b)
            if overlap:
                bad(a["id"], 8, f"area overlaps in-progress {b['id']} ({'; '.join(overlap)})")
            if b["id"] in a["conflicts_with"]:
                bad(a["id"], 10, f"in progress together with conflicting {b['id']}")
    return problems


# ---------------------------------------------------------------- BOARD.md

def node(task_id):
    return task_id.replace("-", "")


def taken(t):
    return t.get("taken_by") or "free"


def parallel_cell(t):
    return "yes" if t.get("parallel") == "yes" else f"no ({', '.join(t['conflicts_with'])})"


def open_questions(t):
    match = re.search(r"\*\*Open questions:\*\*\s*(.*)", t["text"])
    value = (match.group(1).strip().rstrip(".") if match else "").lower()
    return "no" if value in ("", "none", "...", "n/a") else "yes"


def render_board(tasks, warnings, now):
    by = {s: sorted((t for t in tasks.values() if t.get("status") == s), key=sort_key) for s in STATUSES}
    by["done"].sort(key=lambda t: t["id"])
    counts = " · ".join(f"{s} {len(by[s])}" for s in STATUSES)
    lines = [
        "# Board", "",
        "> Generated by `kanban/scripts/board.py` (`/kanban-board`) from `kanban/tasks/*.md`. Don't edit by hand; the",
        "> task files are the source of truth. Rules: `kanban/README.md`.", "",
        f"Updated: {now:%Y-%m-%d %H:%M} · Tasks: {len(tasks)} total · {counts}", "",
        "## Review (waiting for the user)", "",
        "| ID | Title | Branch | In review since | Open questions |", "|---|---|---|---|---|",
        *[f"| {t['id']} | {t.get('title', '')} | {t.get('branch', '')} | {last_move_to(t, 'review')} | "
          f"{open_questions(t)} |" for t in by["review"]],
        "", "## In progress", "",
        "| ID | Title | Priority | Taken by | Owner | Branch | Since |", "|---|---|---|---|---|---|---|",
        *[f"| {t['id']} | {t.get('title', '')} | {t.get('priority', '')} | {taken(t)} | {t.get('owner', '')} | "
          f"{t.get('branch', '')} | {last_move_to(t, 'in-progress')} |" for t in by["in-progress"]],
        "", "## Blocked", "",
        "| ID | Title | Priority | Blocked by (not done yet) | Parallel | Taken by |", "|---|---|---|---|---|---|",
        *[f"| {t['id']} | {t.get('title', '')} | {t.get('priority', '')} | {', '.join(not_done(t, tasks))} | "
          f"{parallel_cell(t)} | {taken(t)} |" for t in by["blocked"]],
        "", "## Backlog", "",
        "Sorted by priority, then ID. The first row is what `/kanban-next` picks.", "",
        "| ID | Title | Priority | Epic | Parallel | Taken by |", "|---|---|---|---|---|---|",
        *[f"| {t['id']} | {t.get('title', '')} | {t.get('priority', '')} | {t.get('epic', '')} | "
          f"{parallel_cell(t)} | {taken(t)} |" for t in by["backlog"]],
        "", "## Done", "",
        "| ID | Title | Approved on |", "|---|---|---|",
        *[f"| {t['id']} | {t.get('title', '')} | {last_move_to(t, 'done')[:10]} |" for t in by["done"]],
        "", "## Dependency graph", "", "```mermaid", "flowchart LR",
    ]
    for t in sorted(tasks.values(), key=lambda t: t["id"]):
        for b in t["blocked_by"]:
            lines.append(f"  {node(b)}[{b}] --> {node(t['id'])}[{t['id']}]")
    lines += ["  classDef done fill:#c8e6c9", "  classDef review fill:#fff9c4", "  classDef inprogress fill:#bbdefb",
              "  classDef backlog fill:#ffffff", "  classDef blocked fill:#eeeeee"]
    for s in ("done", "review", "in-progress", "backlog", "blocked"):
        if by[s]:
            lines.append(f"  class {','.join(node(t['id']) for t in by[s])} {s.replace('-', '')}")
    lines += ["```", "", "## Board warnings", ""]
    lines += [f"- {w}" for w in warnings] or ["none"]
    return "\n".join(lines) + "\n"


def cmd_board(tasks, warnings, now):
    new = render_board(tasks, warnings, now)
    old = BOARD_PATH.read_text(encoding="utf-8") if BOARD_PATH.exists() else ""
    strip = lambda s: re.sub(r"^Updated: \S+ \S+ · ", "", s, count=1, flags=re.M)
    if strip(old) == strip(new):
        return False
    BOARD_PATH.write_text(new, encoding="utf-8")
    return True


# ---------------------------------------------------------------- new-task scans

def mentioned_ids(task):
    body = task["text"].partition("\n---\n")[2]
    body = re.split(r"^## (?:Hand-off|Review|Activity log)$", body, flags=re.M)[0]
    return set(re.findall(r"T-\d+", body)) - {task["id"]}


def cmd_blockers(tasks, task_id):
    task = tasks.get(task_id)
    if not task:
        sys.exit(f"unknown task {task_id}")
    below = descendants(task_id, tasks)
    above = ancestors(task_id, tasks)
    mentions = mentioned_ids(task)
    print(f"{task_id} · {task.get('title', '')} · status {task.get('status')} · "
          f"blocked_by [{', '.join(task['blocked_by'])}] · area {task['area']}")
    print("\nProbably blocked by (unfinished tasks it doesn't list yet). Add a real ordering need to blocked_by;")
    print("a pure file overlap with no ordering need goes to conflicts_with instead:")
    found = False
    for other in sorted(tasks.values(), key=lambda t: t["id"]):
        oid = other["id"]
        if oid == task_id or oid in above or oid in below or other.get("status") == "done":
            continue
        reasons = []
        overlap = areas_overlap(task, other)
        if overlap:
            reasons.append(f"area overlap ({'; '.join(overlap[:3])})")
        if oid in mentions:
            reasons.append("named in its Context/Scope/notes")
        if reasons:
            found = True
            hint = " · already in conflicts_with" if oid in task["conflicts_with"] else ""
            print(f"  {oid}\t{other.get('status')}\t{other.get('title', '')}\t{' + '.join(reasons)}{hint}")
    if not found:
        print("  none found")
    print("\nMay be blocked by it (unfinished tasks that name it but don't depend on it yet):")
    found = False
    for other in sorted(tasks.values(), key=lambda t: t["id"]):
        oid = other["id"]
        if (oid == task_id or task_id in other["blocked_by"] or oid in above
                or other.get("status") in ("done", "review", "in-progress")):
            continue
        if task_id in mentioned_ids(other):
            found = True
            hint = " · already in conflicts_with" if oid in task["conflicts_with"] else ""
            print(f"  {oid}\t{other.get('status')}\t{other.get('title', '')}\tnames {task_id}{hint}")
    if not found:
        print("  none found")
    missing = not_done(task, tasks)
    expected = "blocked" if missing else "backlog"
    if task.get("status") in ("backlog", "blocked") and task.get("status") != expected:
        print(f"\nstatus should be `{expected}`" + (f" (waiting on {', '.join(missing)})" if missing else "")
              + "; `board.py sync` fixes it")


def cmd_unblocks(tasks, task_id):
    if task_id not in tasks:
        sys.exit(f"unknown task {task_id}")
    waiting = [t for t in sorted(tasks.values(), key=sort_key) if task_id in t["blocked_by"]]
    if not waiting:
        print(f"no task is blocked by {task_id}")
    for t in waiting:
        others = [b for b in not_done(t, tasks) if b != task_id]
        state = "ready once it is done" if not others else f"still waits on {', '.join(others)}"
        print(f"{t['id']}\t{t.get('priority', '')}\t{t.get('status')}\t{t.get('title', '')}\t{state}")


# ---------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    for name in ("sync", "merged"):
        p = sub.add_parser(name)
        p.add_argument("--ref", default="main", help="branch whose merge commits count (default main)")
        p.add_argument("--no-github", action="store_true", help="don't ask GitHub for merged PRs")
    sub.add_parser("unblock")
    sub.add_parser("check")
    sub.add_parser("board")
    for name in ("blockers", "unblocks"):
        sub.add_parser(name).add_argument("task_id")
    args = parser.parse_args()

    tasks = load_tasks()
    now = datetime.now()
    if args.cmd in ("blockers", "unblocks"):
        (cmd_blockers if args.cmd == "blockers" else cmd_unblocks)(tasks, args.task_id.upper())
        return
    if args.cmd in ("sync", "merged"):
        cmd_merged(tasks, now, args.ref, not args.no_github)
    if args.cmd in ("sync", "unblock"):
        cmd_unblock(tasks, now)
    problems = cmd_check(tasks)
    if args.cmd in ("sync", "board"):
        if cmd_board(tasks, problems, now):
            print("board: kanban/BOARD.md regenerated", file=sys.stderr)
    if args.cmd in ("sync", "check"):
        for p in problems:
            print(f"warning: {p}", file=sys.stderr)
    if args.cmd == "check" and problems:
        sys.exit(1)


if __name__ == "__main__":
    main()
