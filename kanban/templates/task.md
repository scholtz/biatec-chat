---
id: T-000
title: Short imperative title
status: backlog          # backlog | in-progress | blocked | review | done
priority: P2             # P0 = critical path · P1 = needed for demo · P2 = should have · P3 = nice to have
epic: ""                 # optional grouping, e.g. "Identity", "Agent", "Gateway", "Web UI"
blocked_by: []           # IDs of tasks that must be done first, e.g. [T-003, T-007]; keep them after they finish
area: []                 # globs this task is expected to change, e.g. ["backend/Biatec.Chat.Api/Auth/**"]
parallel: yes            # yes = no other unfinished task that could run at the same time changes the same files; no = see conflicts_with
conflicts_with: []       # when parallel: no, the task IDs it must not run at the same time as (keep the list symmetric)
issue: ""               # GitHub issue in scholtz/biatec-chat, e.g. "#12"; the task file stays the source of truth
branch: ""               # task/T-000 once work has started
owner: ""                # set while in-progress/review; empty in backlog/blocked
taken_by: ""             # GitHub username of the teammate who took the task (/kanban-take); kept until done
source: "PRODUCT-DESCRIPTION.md#section-anchor"
created: YYYY-MM-DD
updated: YYYY-MM-DD
---

# T-000 · Short imperative title

## Context

Why this task exists and where it fits. Link the product-definition section(s) and any guide sections
(e.g. `docs/AWS-AGENTCORE-GUIDE.md` §4.2) the implementer must read. Quote the requirement it implements, so the
task can be worked without reading the whole product definition.

## Goal

One or two sentences describing the outcome, not the steps.

## Scope

- In: what this task changes or delivers.
- Out: what it deliberately leaves to other tasks (name them by ID when known).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

(Or: Parallel work possible: **no**. Don't run it at the same time as T-###, T-###: they change the same files, and
running them together causes merge conflicts or broken behaviour.)

## Acceptance criteria

Each criterion must be checkable by running something or looking at something specific.

- [ ] ...
- [ ] ...

## Implementation notes

Known constraints, decisions already made, files likely touched, pitfalls. Optional.

## Verification

Exact commands or manual steps that show the acceptance criteria are met (tests, curl calls, screenshots to take).

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-###`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

Written only by the user (or by `/kanban-review` on the user's behalf). Each round adds a dated entry with
`approve` or `changes` and the feedback.

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- YYYY-MM-DD HH:MM · kanban-split · (new) → backlog · created from product definition
