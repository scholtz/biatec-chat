#!/usr/bin/env python3
"""List `parallel: yes` tasks that are ready to start, optionally syncing the board first.

A task is ready when its status is `backlog` or `blocked`, every ID in `blocked_by` is `done`
(an empty `blocked_by` counts), and nobody has taken it (`taken_by` is empty). With `--user`, tasks
taken by that GitHub user are listed too. `--unblock` first runs `board.py sync`: task branches merged
into main become `done`, blocked tasks whose blockers are done move to `backlog` (and backlog tasks
waiting on an unfinished blocker move to `blocked`), and `kanban/BOARD.md` is regenerated.

Usage:
  kanban/scripts/list-parallel-ready.py                  # list untaken ready tasks, changes nothing
  kanban/scripts/list-parallel-ready.py --user octocat   # also list the ones octocat has taken
  kanban/scripts/list-parallel-ready.py --unblock        # sync the board (any parallel flag), then list
"""
import argparse
import sys
from datetime import datetime

sys.dont_write_bytecode = True
from board import blockers_done, cmd_board, cmd_check, cmd_merged, cmd_unblock, load_tasks, sort_key


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--unblock", action="store_true",
                        help="sync the board (merged → done, fix blocked state, regenerate BOARD.md) before listing")
    parser.add_argument("--user", metavar="GITHUB_USER",
                        help="also list tasks taken by this GitHub user (others' tasks are always hidden)")
    args = parser.parse_args()

    tasks = load_tasks()
    now = datetime.now()

    if args.unblock:
        cmd_merged(tasks, now)
        cmd_unblock(tasks, now)
        if cmd_board(tasks, cmd_check(tasks), now):
            print("board: kanban/BOARD.md regenerated", file=sys.stderr)
    else:
        for t in tasks.values():
            if t.get("status") == "blocked" and t["blocked_by"] and blockers_done(t, tasks):
                print(f"stale: {t['id']} is blocked but its blockers are done; run with --unblock", file=sys.stderr)

    ready = [t for t in tasks.values()
             if t.get("parallel") == "yes" and t.get("status") in ("backlog", "blocked")
             and t.get("taken_by", "") in ("", args.user) and blockers_done(t, tasks)]
    ready.sort(key=sort_key)
    for t in ready:
        blocked_by = ", ".join(t["blocked_by"])
        print(f"{t['id']}\t{t.get('priority', '')}\t{t.get('status')}\t{t.get('title', '')}\tblocked_by: [{blocked_by}]"
              f"\ttaken_by: {t.get('taken_by', '')}")


if __name__ == "__main__":
    main()
