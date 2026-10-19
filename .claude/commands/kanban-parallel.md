---
description: Unblock tasks whose blockers are done, then list the parallel: yes tasks that are ready and not taken by a teammate
allowed-tools: Bash(kanban/scripts/list-parallel-ready.py:*), Bash(gh api user:*)
---

List the tasks you can pick up in parallel right now. The only change this command makes is unblocking.

1. Get your GitHub username: `gh api user -q .login` (fallback `git config github.user`). Call it `<me>`. Run
   `git pull --rebase origin main` first if the working tree is clean, so that teammates' takes are visible.
2. Run `kanban/scripts/list-parallel-ready.py --unblock --user <me>`. It moves every `blocked` task whose
   `blocked_by` tasks are all `done` to `backlog` (reported on stderr as `unblocked: T-###`), then prints one line
   per ready task: `ID<TAB>priority<TAB>status<TAB>title<TAB>blocked_by: [...]<TAB>taken_by: <user>`. Ready means
   `parallel: yes`, every `blocked_by` task is `done` (or there are none), and `taken_by` is empty or `<me>`.
3. If anything was unblocked, regenerate `kanban/BOARD.md` (the `/kanban-board` steps 3 and 4) and commit
   `kanban: unblock T-###, …` on `main`.
4. Show the ready tasks as a table with the columns ID, Priority, Title, Blocked by and Taken by (`you` or `free`),
   in the script's order (priority, then ID), and name the tasks that were unblocked in this run. Suggest
   `/kanban-take T-### …` for the free ones the user wants.
5. If no task is ready, say so and list what the remaining `parallel: yes` tasks are waiting on.
