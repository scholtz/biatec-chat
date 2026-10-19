---
description: Move one kanban task to another status, checked against the board rules
argument-hint: <T-###> <backlog|in-progress|blocked|review|done> [note or blocking IDs]
---

Move a single task: `$ARGUMENTS` (the task ID, then the target status, then an optional note).

1. Read `kanban/README.md` and the task file. Look the transition up in the **Allowed transitions** table and check
   its condition against the real task files, not `BOARD.md`.
2. Special cases:
   - **→ done**, or anything starting from `review`: refuse and point to `/kanban-review`. Only the user approves.
   - **→ blocked**: the note must name the blocking task IDs. Add them to `blocked_by` (each must exist, must not be
     this task, and must not create a cycle). Clear `owner`.
   - **→ backlog** from `blocked`: every `blocked_by` task must be `done`.
   - **→ in-progress**: set `owner`, and make sure that owner has no other `in-progress` task and that no task in
     `conflicts_with` is `in-progress`.
   - **→ review**: every acceptance criterion is ticked, or explained under Hand-off, and every Hand-off field is
     filled in.
3. If the move isn't allowed, change nothing and explain which rule fails.
4. Otherwise update `status`, `owner` and `updated`, append the Activity log line with the note, and regenerate
   `kanban/BOARD.md` the way `/kanban-board` does. Report the move in one line.
