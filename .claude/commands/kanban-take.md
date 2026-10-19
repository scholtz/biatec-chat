---
description: Take kanban tasks for yourself (mark them taken_by your GitHub user), or release them, and push the board
argument-hint: "<T-### [T-### …]> | release <T-### [T-### …]>"
---

Mark tasks as taken by the teammate running this session, so that nobody else on the team picks them up. Rules:
`kanban/README.md` → "Team: who takes which task". Arguments: `$ARGUMENTS`

Invoking this command is the user's go-ahead to commit the board on `main` and push it to `origin main`, because a
reservation nobody else can see doesn't work. Push nothing else.

1. **Who.** The GitHub username is the output of `gh api user -q .login`. If that fails, use `git config github.user`;
   if that is empty too, ask the user. Call it `<me>`.
2. **Sync.** Make sure you are on `main` in the main checkout, then run `git pull --rebase origin main` so that you
   see what teammates took. If the working tree has uncommitted changes under `kanban/` that aren't yours to commit,
   stop and ask.
3. **Check** each task ID against its file:
   - **Take:** the file exists, `status` isn't `done`, and `taken_by` is empty or already `<me>`. If a teammate has
     it, skip it and say who. A `blocked` task may be taken: it reserves the task for when its blockers are done.
   - **Release** (`release T-###`): `taken_by` must be `<me>`, and `status` must not be `in-progress` or `review`
     (finish or hand the work off first). Never release a teammate's task; ask them to.
4. **Mark.** Set `taken_by: "<me>"` (or `taken_by: ""` when releasing) and `updated`, and append to the Activity log
   `- <YYYY-MM-DD HH:MM> · <me> · taken · status stays <status>` (or `· released`). `status` and `owner` don't
   change: taking is a reservation, and claiming still happens in `/kanban-next` or `/kanban-dispatch`.
5. **Board.** Run the `/kanban-board` steps.
6. **Commit and push.** `git add kanban && git commit -m "kanban: <me> takes T-###, …"` (or `releases`), then
   `git push origin main`. If the push is rejected, run `git pull --rebase origin main`. If the rebase conflicts on a
   task file because a teammate took the same task first, keep the remote version of that file (during a rebase
   that is `git checkout --ours <file>`), drop the task from your list, regenerate `BOARD.md`, `git rebase
   --continue`, and push again.
7. **Report** the tasks you took or released, the ones skipped and who holds them, and the result of
   `kanban/scripts/list-parallel-ready.py --user <me>`, so the user sees what else is free.
