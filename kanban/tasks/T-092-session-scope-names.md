---
id: T-092
title: "Session endpoint: return the session's OIDC scope names"
status: done
priority: P3
epic: "Sessions & auth"
blocked_by: [T-083]
area: ["backend/Biatec.Chat.Api/Sessions/SessionEndpoints.cs", "backend/Biatec.Chat.Tests/Sessions/**"]
parallel: no
conflicts_with: [T-095]
issue: "#179"
branch: "task/T-092"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-092 · Session endpoint: return the session's OIDC scope names

## Context

Proposed by the T-083 worker. `SessionEndpoints.cs` still returns `Scopes = []` although `TokenVaultEntry.Scopes` holds the scope names and the session contract documents them; the widget will need them to show "Authorize signing" only when `sign` is missing.

## Goal

GET /session returns the scope names the session holds (never tokens).

## Scope

- In: read the scope names with the cached session profile from T-083 (no extra vault decrypt per poll); guests get an empty list.
- Out: widget changes.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-095: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] A signed-in session returns its scope names; a guest returns []
- [ ] No extra vault read per poll (test)

## Verification

`dotnet test --filter Session`, then the full backend suite.

## Hand-off

## Review

- 2026-10-20 07:50 · approve · PR #199 merged into main @ 2e6174c (recorded by kanban-sync)

## Activity log

- 2026-10-20 04:16 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 05:31 · coordinator · backlog · conflicts_with + T-095 (blockers scan: shared files)
- 2026-10-20 06:19 · scholtz · taken · status stays backlog
- 2026-10-20 06:19 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 07:50 · user · in-progress → done · PR #199 merged into main @ 2e6174c (synced by kanban-sync)
