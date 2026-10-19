---
description: Unblock tasks whose blockers are done, check board invariants, and regenerate kanban/BOARD.md
---

Rebuild the board from the task files. The rules are in `kanban/README.md`.

1. Read the frontmatter of every `kanban/tasks/T-*.md` file.
2. **Unblock.** Run `kanban/scripts/list-parallel-ready.py --unblock`. For each `blocked` task whose `blocked_by`
   tasks are all `done` (whatever made them `done`, including a PR merged on GitHub), it sets `status: backlog`,
   updates `updated`, and appends `- <now> · kanban-board · blocked → backlog · blockers done: <IDs>` to the log.
   It reports each unblocked task on stderr. If the script can't run, make the same edits by hand. This is the only
   status change this command makes; re-read the task files afterwards.
3. **Check** every invariant in `kanban/README.md`. Don't fix violations silently. List each one with the task ID,
   the rule broken, and the fix you suggest.
4. **Regenerate** `kanban/BOARD.md` from `kanban/templates/board.md`:
   - Review: the time in review comes from the log line where the task moved to review. Show open questions as
     "yes" or "no".
   - Blocked: list only the blockers that aren't done yet.
   - Backlog: sort by priority (P0 first), then ID.
   - Backlog and Blocked: the Parallel column shows `yes`, or `no (T-###, …)` with the `conflicts_with` IDs.
   - In progress, Blocked and Backlog: the Taken by column shows `taken_by`, or `free` when it is empty.
   - Done: the approval date comes from the log.
   - Mermaid graph: one `BLOCKER --> BLOCKED` edge per `blocked_by` entry. Label each node `T-###` and use
     `classDef` colours per status.
   - Fill in the header counts and the Board warnings section.
5. Report the counts per status, the tasks unblocked in this run, the tasks waiting for the user's review, and any
   warnings.
