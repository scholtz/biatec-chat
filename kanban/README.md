# Kanban: how work is tracked

A file-based board. Every task is one markdown file in `kanban/tasks/`. Its YAML frontmatter is the **single source
of truth** for the task's status. `kanban/BOARD.md` is only a view and is regenerated from the task files with
`/kanban-board`. Nothing here is enforced automatically. Every agent is expected to follow these rules, and the
slash commands in `.claude/commands/` apply them.

## Files

| Path | Purpose |
|---|---|
| `kanban/tasks/T-###-slug.md` | One task, created from `kanban/templates/task.md` |
| `kanban/BOARD.md` | Generated board view, created from `kanban/templates/board.md` |
| `kanban/templates/` | Templates; copy them, never edit them in place for a task |
| `PRODUCT-DESCRIPTION.md` | The specification that tasks are split from (`source:` links point here) |

## Statuses

| Status | Meaning | Who moves a task **into** it |
|---|---|---|
| `backlog` | Ready to start: every task in `blocked_by` is `done` | `/kanban-split`, the coordinator creating a follow-up task, or `/kanban-board` unblocking it |
| `in-progress` | Claimed and being worked on (`owner` is set) | `/kanban-dispatch` (for a worker) or `/kanban-next` |
| `blocked` | Can't start or continue because at least one task in `blocked_by` isn't `done` | `/kanban-split` at creation, or the coordinator when a worker reports a dependency |
| `review` | The work is finished on its branch and **waiting for the user** to approve or request changes | The coordinator, or the agent running `/kanban-next` |
| `done` | Approved by the user, and its branch merged into `main` | **Only the user**, via `/kanban-review <id> approve` |

## Allowed transitions

```
backlog ──claim──▶ in-progress ──hand-off──▶ review ──user approves + merge──▶ done
   ▲  │                │   ▲                    │
   │  └──▶ blocked ◀───┘   └──user requests ────┘
   │          │                changes
   └──────────┘  (all blocked_by are done)
```

| From → To | Condition |
|---|---|
| backlog → in-progress | Every `blocked_by` task is `done`; `owner` is empty; this owner has no other `in-progress` task; `area` doesn't overlap any other `in-progress` task's `area` |
| backlog → blocked | A dependency was found: add its ID to `blocked_by` |
| in-progress → blocked | The work needs another unfinished task. Add the ID to `blocked_by`, write the progress so far in the log, and clear `owner`. The branch is kept |
| in-progress → backlog | The worker failed or gave up without a result. The log says why; clear `owner`, keep the branch if it has useful commits |
| blocked → backlog | Every `blocked_by` task is `done`. `/kanban-board` does this automatically |
| in-progress → review | Every acceptance criterion is checked, or explained in the Hand-off; verification was run; the **Hand-off** section is filled in; the work is committed on the task branch |
| review → done | **The user approved it** and the task branch was merged into `main`. An agent may do this only while running `/kanban-review <id> approve` that the user typed |
| review → backlog | The user requested changes (`/kanban-review <id> changes ...`). The feedback goes in the **Review** section; `owner` is cleared and the existing branch is kept, so the next dispatch resumes it |

Any other move is not allowed. `done` is final: to rework a finished task, create a new task that lists the old one
in `blocked_by`, or that links it in Context.

## Invariants (checked by `/kanban-board`)

1. `status` is one of `backlog`, `in-progress`, `blocked`, `review`, `done`.
2. `backlog` and `in-progress`: every ID in `blocked_by` is `done`.
3. `blocked`: `blocked_by` is non-empty and at least one of its tasks isn't `done`.
4. `in-progress` and `review`: `owner` is set. `backlog` and `blocked`: `owner` is empty.
5. Every ID in `blocked_by` exists, no task blocks itself, and the dependency graph has no cycles.
6. `done`: the last Activity log entry records the user's approval and the merge.
7. The file name `T-###-slug.md` matches the `id`. IDs are never reused.
8. No two `in-progress` tasks have overlapping `area` globs.

## Parallel work: branches, areas, coordinator

- **Branch per task.** All work for `T-###` happens on the git branch `task/T-###`, cut from `main`. The `branch`
  field records it. Approving a task merges that branch into `main` (`git merge --no-ff`), which is what makes
  dependent tasks' inputs available.
- **`area`** lists the paths a task is expected to change (globs). Two tasks with overlapping areas never run at the
  same time, which keeps merges clean. A worker that has to touch files outside its area reports it in the Hand-off.
- **The coordinator owns the board.** `/kanban-dispatch` runs in the main checkout, claims tasks, launches one
  background worker agent per task in its own git worktree, and records each worker's result. Workers **never edit
  anything under `kanban/`**. They report their hand-off, blockers and proposed follow-up tasks back to the
  coordinator, which writes them into the task files. This avoids merge conflicts on board files and duplicate IDs.
- **GitHub issues.** Each task has a mirror issue in `scholtz/biatec-chat`, recorded in its `issue` field. The
  issue is for visibility only: status lives in the task file, and the task's PR or merge commit references it
  (`Refs #N`). A new task gets its issue when the coordinator creates it.
- **Commits.** Workers commit their work on their task branch. The coordinator commits board changes on `main`
  (message `kanban: ...`), because worktrees are created from the last commit. Nothing is ever pushed unless the
  user asks.

## Rules for every agent

- **Before claiming**, re-read the task file. If `owner` is set or `status` isn't `backlog`, pick another task.
- **Stay in scope.** If you find work outside the task's scope, propose a follow-up task instead of expanding this
  one. A worker proposes it in its report; an agent running `/kanban-next` creates it from the template.
- **Every status change** updates `status`, `updated` and `owner`, and appends one line to the Activity log:
  `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`. The actor is the owner value, `coordinator`, or `user`.
- **Questions for the user** don't make a task `blocked`, because blocked means blocked by tasks. Ask in the
  conversation. A background worker records the question under Hand-off → Open questions, finishes what it can,
  and hands off to `review`.
- **Never set `done`**, and never edit the **Review** section, except while running `/kanban-review` invoked by
  the user.
- Rebuild `BOARD.md` (the `/kanban-board` steps) after any status change. Never hand-merge `BOARD.md`;
  regenerate it.

## Commands

| Command | Who | What |
|---|---|---|
| `/kanban-split [file.md]` | user | Split the product description into task files and build the board |
| `/kanban-dispatch [N]` | user | Coordinator: run up to N worker agents in parallel worktrees until no task is ready |
| `/kanban-next [T-###]` | user | Work one task in the current session, on its own branch |
| `/kanban-move <T-###> <status> [note]` | user or coordinator | Make one manual move, checked against the transition table |
| `/kanban-board` | anyone | Unblock tasks whose blockers are done, check the invariants, regenerate `BOARD.md` |
| `/kanban-review [T-### approve \| changes <feedback>]` | **user only** | List tasks waiting for review, or approve (merge) a task or send it back |
