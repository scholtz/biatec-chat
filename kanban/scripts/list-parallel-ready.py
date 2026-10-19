#!/usr/bin/env python3
"""List `parallel: yes` tasks that are ready to start, optionally unblocking tasks first.

A task is ready when its status is `backlog` or `blocked`, every ID in `blocked_by` is `done`
(an empty `blocked_by` counts), and nobody has taken it (`taken_by` is empty). With `--user`, tasks
taken by that GitHub user are listed too. A `blocked` task whose blockers are done is stale:
`--unblock` moves it to `backlog` the way `/kanban-board` does (status, `updated`, Activity log line).

Usage:
  kanban/scripts/list-parallel-ready.py                  # list untaken ready tasks, changes nothing
  kanban/scripts/list-parallel-ready.py --user octocat   # also list the ones octocat has taken
  kanban/scripts/list-parallel-ready.py --unblock        # unblock every stale task (any parallel flag), then list
"""
import argparse
import re
import sys
from datetime import datetime
from pathlib import Path

TASKS_DIR = Path(__file__).resolve().parent.parent / "tasks"
PRIORITY_ORDER = {"P0": 0, "P1": 1, "P2": 2, "P3": 3}


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


def load_tasks():
    tasks = {}
    for path in sorted(TASKS_DIR.glob("T-*.md")):
        text = path.read_text(encoding="utf-8")
        fm = read_front_matter(text)
        if "id" in fm:
            fm["blocked_by"] = parse_id_list(fm.get("blocked_by"))
            tasks[fm["id"]] = {"path": path, "text": text, **fm}
    return tasks


def blockers_done(task, tasks):
    for blocker in task["blocked_by"]:
        if blocker not in tasks:
            print(f"warning: {task['id']} is blocked by unknown task {blocker}", file=sys.stderr)
            return False
        if tasks[blocker].get("status") != "done":
            return False
    return True


def unblock(task, now):
    head, sep, body = task["text"].partition("\n---\n")
    head = re.sub(r"^status:.*$", "status: backlog", head, count=1, flags=re.M)
    head = re.sub(r"^updated:.*$", f"updated: {now:%Y-%m-%d}", head, count=1, flags=re.M)
    log_line = (f"- {now:%Y-%m-%d %H:%M} · kanban-board · blocked → backlog · "
                f"blockers done: {', '.join(task['blocked_by'])}")
    text = head + sep + body.rstrip("\n") + "\n" + log_line + "\n"
    task["path"].write_text(text, encoding="utf-8")
    task["text"], task["status"] = text, "backlog"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--unblock", action="store_true",
                        help="move blocked tasks whose blockers are all done to backlog before listing")
    parser.add_argument("--user", metavar="GITHUB_USER",
                        help="also list tasks taken by this GitHub user (others' tasks are always hidden)")
    args = parser.parse_args()

    tasks = load_tasks()
    now = datetime.now()

    stale = [t for t in tasks.values() if t.get("status") == "blocked" and blockers_done(t, tasks)]
    for task in stale:
        if args.unblock:
            unblock(task, now)
            print(f"unblocked: {task['id']} (blockers done: {', '.join(task['blocked_by'])})", file=sys.stderr)
        else:
            print(f"stale: {task['id']} is blocked but its blockers are done; run with --unblock", file=sys.stderr)

    ready = [t for t in tasks.values()
             if t.get("parallel") == "yes" and t.get("status") in ("backlog", "blocked")
             and t.get("taken_by", "") in ("", args.user) and blockers_done(t, tasks)]
    ready.sort(key=lambda t: (PRIORITY_ORDER.get(t.get("priority"), 9), t["id"]))
    for t in ready:
        blocked_by = ", ".join(t["blocked_by"])
        print(f"{t['id']}\t{t.get('priority', '')}\t{t.get('status')}\t{t.get('title', '')}\tblocked_by: [{blocked_by}]"
              f"\ttaken_by: {t.get('taken_by', '')}")


if __name__ == "__main__":
    main()
