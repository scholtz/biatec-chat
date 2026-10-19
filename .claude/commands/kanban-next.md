---
description: Work one kanban task in this session (claim, implement on its branch, hand off for review)
argument-hint: "[T-###]"
---

Work one kanban task end to end in this session. The rules are in `kanban/README.md`; read it first. To run several
tasks in parallel, use `/kanban-dispatch` instead.

1. **Pick.** Run `git pull --rebase origin main`, get your GitHub username `<me>` (`gh api user -q .login`), and
   run `kanban/scripts/board.py sync` so that merged tasks are `done` and tasks whose blockers are done are back in
   `backlog` (if `kanban/` changed, commit `kanban: sync board …`). Then
   `kanban/scripts/list-parallel-ready.py --user <me>` lists the ready `parallel: yes` tasks.
   If `$ARGUMENTS` names a task, use it. Otherwise pick the `backlog` task taken by `<me>`, or else untaken, with the
   highest priority, then the lowest ID. Before claiming, re-read the file and confirm that:
   - `status: backlog` and `owner` is empty;
   - `taken_by` is empty or `<me>`; a teammate's task is never yours to pick, even when named in `$ARGUMENTS`;
   - every ID in `blocked_by` has `status: done` (read those files; don't trust `BOARD.md`);
   - its `area` doesn't overlap any `in-progress` task;
   - if it is `parallel: no`, no task in its `conflicts_with` is `in-progress`. Otherwise pick the next task.

   If nothing is ready, run the `/kanban-board` steps, report what is blocked on what, and stop.
2. **Claim on `main`.** If `taken_by` is empty, take the task first with the `/kanban-take` steps (it commits and
   pushes, so teammates see it). Set `status: in-progress`, `owner: session-YYYYMMDD-HHMM`, `branch: task/T-###` and
   `updated`; append the log line; run `kanban/scripts/board.py sync`; and commit `kanban: claim T-###`. Then
   `git switch -c task/T-###` (or switch to the existing branch).
3. **Work.** Read the task's Context links, `AGENTS.md`, and the code that its `blocked_by` tasks merged. Implement
   only the Scope; commit on the branch with messages starting `T-###: `.
   - Extra work found: create a new task from the template (next free ID) on `main` afterwards, and scan it for
     blockers as in `kanban/README.md` → "Creating a task" (`kanban/scripts/board.py blockers T-###`, decide each
     candidate, then `board.py sync`).
   - Need for an unfinished task: stop, switch to `main`, add the ID to `blocked_by`, move the task to `blocked`,
     clear `owner`, log the progress, and run `board.py sync`.
4. **Verify.** Run the Verification steps. Tick a criterion only after actually confirming it.
5. **Hand off.** Switch back to `main`. Run `kanban/scripts/board.py unblocks T-###` and fill in the Hand-off
   (include `branch @ commit`, and the **Unblocks:** line with the tasks that become ready once this one is
   approved). Set `status: review` (keep `owner`), log it, run `board.py sync`, and commit `kanban: T-### → review`.
   Never set `done`.
6. **Report** what changed, the verification results, unmet criteria, open questions, new tasks, and what the task
   unblocks once approved. Point the user to `/kanban-review <id> approve` (or merging the PR on GitHub; the
   `kanban-sync` Action then marks it done and unblocks its dependants) or `/kanban-review <id> changes <feedback>`.
