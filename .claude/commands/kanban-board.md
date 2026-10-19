---
description: Unblock tasks whose blockers are done, check board invariants, and regenerate kanban/BOARD.md
---

Sync the board with `main` and the task files. The rules are in `kanban/README.md` ("Automatic sync").

1. If the working tree is clean, run `git pull --rebase origin main` first, so merges and teammates' changes are
   visible.
2. Run `kanban/scripts/board.py sync`. In one pass it:
   - records every `task/T-###` branch merged into `main` (merged GitHub PR, any merge method, or merge commit) as
     `done` (stderr: `done: T-###`);
   - moves `blocked` tasks whose `blocked_by` are all `done` to `backlog` (`unblocked: T-###`), and `backlog` tasks
     with an unfinished blocker to `blocked` (`blocked: T-###`);
   - regenerates `kanban/BOARD.md` from the task files;
   - reports invariant violations as `warning: T-###: invariant N: …` (also written into Board warnings).
   If the script can't run, make the same edits by hand, following `kanban/README.md`.
3. Don't fix invariant violations silently. List each one with the task ID, the rule broken, and the fix you
   suggest.
4. If anything under `kanban/` changed, commit it on `main`: `kanban: sync board (T-### done, unblock T-###, …)`.
5. Report the counts per status, the tasks marked done, unblocked or re-blocked in this run, the tasks waiting for
   the user's review, and any warnings. For every task that became `done`, also run
   `kanban/scripts/board.py unblocks T-###` and name the tasks that are now ready.
