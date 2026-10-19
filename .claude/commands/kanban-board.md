---
description: Unblock tasks whose blockers are done, check board invariants, and regenerate kanban/BOARD.md
---

Rebuild the board from the task files. The rules are in `kanban/README.md`.

1. Read the frontmatter of every `kanban/tasks/T-*.md` file.
2. **Unblock.** For each `blocked` task whose `blocked_by` tasks are all `done`: set `status: backlog`, update
   `updated`, and append `- <now> · kanban-board · blocked → backlog · blockers done: <IDs>` to its log.
   This is the only status change this command makes.
3. **Check** every invariant in `kanban/README.md`. Don't fix violations silently. List each one with the task ID,
   the rule broken, and the fix you suggest.
4. **Regenerate** `kanban/BOARD.md` from `kanban/templates/board.md`:
   - Review: the time in review comes from the log line where the task moved to review. Show open questions as
     "yes" or "no".
   - Blocked: list only the blockers that aren't done yet.
   - Backlog: sort by priority (P0 first), then ID.
   - Done: the approval date comes from the log.
   - Mermaid graph: one `BLOCKER --> BLOCKED` edge per `blocked_by` entry. Label each node `T-###` and use
     `classDef` colours per status.
   - Fill in the header counts and the Board warnings section.
5. Report the counts per status, the tasks unblocked in this run, the tasks waiting for the user's review, and any
   warnings.
