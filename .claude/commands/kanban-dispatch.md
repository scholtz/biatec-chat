---
description: Coordinator - run ready kanban tasks in parallel worker agents (one git worktree and branch each) until nothing is ready
argument-hint: "[max parallel workers, default 3]"
---

You are the **coordinator**. You own the board; workers never touch `kanban/`. Read `kanban/README.md` (especially
"Parallel work") and `AGENTS.md` first. Maximum parallel workers: `$ARGUMENTS` (default 3, never more than 5,
because the team shares a 50 req/min Bedrock limit and the user reviews everything).

Invoking this command is the user's go-ahead for the commits described here: board commits on `main`, and work
commits on `task/T-###` branches. Never push and never merge; merging happens only in `/kanban-review`.

## Loop

1. **Sync the board.** Run the `/kanban-board` steps. If `git status` shows uncommitted changes outside `kanban/`
   on `main`, stop and ask the user, because worktrees would not include them. Commit any `kanban/` changes:
   `git add kanban && git commit -m "kanban: sync board"`.
2. **Select tasks.** Take `backlog` tasks in board order (priority, then ID). Pick each one whose `area` doesn't
   overlap the `area` of any `in-progress` task or of any task already picked, and whose `conflicts_with` names no
   `in-progress` task and no task already picked. A `parallel: yes` task only needs the area check. Skip a
   conflicting task for now (don't change its status) and take it once the other one leaves `in-progress`. Stop
   when the running plus picked workers reach the maximum.
3. **Claim each selected task on `main`.** Set `status: in-progress`, `owner: worker-T-###`, `branch: task/T-###`
   and `updated`, then append `- <now> · coordinator · backlog → in-progress · dispatched to worker`. Regenerate
   the board and commit: `kanban: dispatch T-###, T-###`.
4. **Launch one background worker per task** with the Agent tool: `subagent_type: general-purpose`,
   `isolation: worktree`, `description: "T-### <short title>"`, and the worker prompt below with the ID filled in.
   If a task already has a `task/T-###` branch with commits (resumed work, or changes requested), tell the worker to
   check out that branch instead of creating it.
5. **Tell the user** which tasks are running. Then wait: worker results arrive as notifications. Don't poll, and
   don't predict results.
6. **On each worker result**, update that task on `main`:
   - `STATUS: review`: paste the report into the task's Hand-off section. Tick the acceptance criteria the worker
     reports as verified. Set `status: review` (keep `owner`) and log
     `in-progress → review · branch task/T-### @ <commit>`.
   - `STATUS: blocked`: add `BLOCKED_BY` to `blocked_by`, set `status: blocked`, clear `owner`, and log the progress
     and next steps the worker reported.
   - `STATUS: failed`, or no usable report: set `status: backlog`, clear `owner`, and log the reason. If the same
     task fails twice, leave it in backlog with a `NEEDS USER:` note and don't dispatch it again.
   - `PROPOSED TASKS`: create each from `kanban/templates/task.md` with the next free ID, status `backlog` or
     `blocked`, and the log line `coordinator · (new) → <status> · proposed by T-###`. Set its `parallel` flag and
     `conflicts_with`, and add it to the `conflicts_with` of the tasks it conflicts with.
   - Files changed outside the task's area: recompute `parallel` / `conflicts_with` for this task and every
     unfinished task that changes those files, on both sides (rules in `kanban/README.md`, "Parallel work").

   Then regenerate the board, commit `kanban: T-### → <status>`, and go back to step 2 to fill the free slot.
7. **Stop** when no worker is running and no task is ready. Report: tasks in review (with branches), blocked tasks
   and their blockers, tasks needing the user, and new tasks created. Remind the user that `/kanban-review` merges
   approved branches, and that the merges are what unblock the next wave.

## Worker prompt (fill in T-###)

```
You are a worker agent for kanban task T-### in the biatec-chat repository. You are running in your own git worktree.

1. Branch: run `git switch -c task/T-###` (or `git switch task/T-###` if it exists and the coordinator said so).
2. Read AGENTS.md, kanban/README.md, your task file kanban/tasks/T-###-*.md, the PRODUCT-DESCRIPTION.md sections it
   links in Context and `source`, and the code that the tasks in its blocked_by already merged.
3. Implement ONLY the task's Scope. Stay inside its `area` globs where possible. Don't modify anything under kanban/
   and don't modify anything outside this repository. Work through the Acceptance criteria and run the Verification
   steps.
4. Commit your work on task/T-### with messages starting "T-###: ". Don't push, don't merge, don't switch to main.
5. If you need another task's unfinished output, or work clearly outside your scope, don't do it: report it.
   If only the user can answer something, make the safest reasonable assumption, note it, and continue.
6. Finish with exactly this report (it is the only thing the coordinator reads):

STATUS: review | blocked | failed
BRANCH: task/T-### @ <short commit hash>
SUMMARY: <what was done, 2-5 lines>
FILES CHANGED: <paths; flag any outside the task's area>
ACCEPTANCE: <each criterion: verified | not met (why)>
VERIFICATION: <commands run and their results>
OPEN QUESTIONS: <for the user, or none>
BLOCKED_BY: <task IDs, only when STATUS is blocked>
PROPOSED TASKS: <title, why, suggested blocked_by and area for each; or none>
```
