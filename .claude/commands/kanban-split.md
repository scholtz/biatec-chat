---
description: Split the product description into kanban task files and build the board
argument-hint: "[path to product description .md, default PRODUCT-DESCRIPTION.md]"
---

Split the product description at `$ARGUMENTS` (default: `PRODUCT-DESCRIPTION.md`) into kanban tasks.

First read `kanban/README.md` (rules), `kanban/templates/task.md` and `kanban/templates/board.md`, and `AGENTS.md`.
Then read the **whole** product description before writing anything.

1. **Existing board.** If `kanban/tasks/` already has tasks, don't overwrite or renumber them. Continue numbering
   after the highest existing ID, skip requirements an existing task already covers, and list those skips in the
   summary. If the description contradicts an existing task, report it instead of changing that task.
2. **Only this repository.** A requirement that needs a change in another repository becomes a task to build the
   documented fallback here, a mock for tests, and an entry in `docs/required-changes-biatec-oidc.md`. Never a task
   that edits another repository.
3. **Epics.** Group the requirements into epics (for example Foundation, Sessions & auth, Orchestrator, Wallet
   tools, DEX, Safe, Evidence, Widget, Consent & authenticator, Deploy & docs).
4. **Tasks.** Each task must be:
   - small enough for one agent session (about half a day at most) and produce one reviewable branch;
   - a vertical, testable slice where possible;
   - complete from the template, with Context (quote the requirement and link `source:` to the exact heading
     anchor), Goal, Scope in/out, 2–6 checkable Acceptance criteria, and concrete Verification steps;
   - self-contained, so an agent can work it from the task file plus the linked sections;
   - given an `area` (path globs). Keep areas of tasks that can run at the same time disjoint, and give shared files
     (solution file, root `package.json`, `docker-compose.yml`) to one early scaffolding task.
5. **Dependencies.** Set `blocked_by` only for real ordering needs. The graph must have no cycles and should be wide;
   several tasks must be startable at once.
6. **Initial status.** Use `backlog` if `blocked_by` is empty, otherwise `blocked`. `owner` and `branch` stay empty.
   `created` and `updated` are today's date. The single Activity log line reads
   `kanban-split · (new) → <status> · created from <description file>`.
7. **Priority.** P0 for the critical path to a working demo before the code freeze in `AGENTS.md`, P1 for things
   needed in the demo, P2 for things that should exist, P3 for nice-to-haves.
8. **Write the files** as `kanban/tasks/T-###-kebab-slug.md`, then build `kanban/BOARD.md` exactly as
   `/kanban-board` does.
9. **Summary to the user.** Report the task count per epic and priority, the critical path, the first wave a
   dispatcher can start in parallel, human prerequisites (things only the user or an operator can do), ambiguous
   requirements with your assumptions, and what you deliberately left out. Don't start any task.
