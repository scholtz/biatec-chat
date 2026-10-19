---
id: T-011
title: Logout, sweeper and pending revocation
status: done
priority: P1
epic: "Sessions & auth"
blocked_by: [T-009]
area: ["backend/Biatec.Chat.Api/Auth/Logout/**", "backend/Biatec.Chat.Api/Sweeper/**"]
parallel: no
conflicts_with: [T-056]
issue: "#12"
branch: "task/T-011"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-011 · Logout, sweeper and pending revocation

## Context

Logout reads tokens, revokes (fallback: delete and let age out), clears cookie, RP-initiated logout at /connect/endsession with id_token_hint; sweeper every minute ends idle (unless open ConsentRequest) and absolute-expired sessions; PendingRevocation retried.

## Goal

Sessions end cleanly and nothing outlives them longer than necessary.

## Scope

- In: Logout endpoint and redirect, background sweeper, PendingRevocation retry worker that is a no-op until a revocation URL is configured, security note in docs.
- Out: OIDC revocation endpoint implementation (other repo).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-056: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Logout clears cookie and vault entry and redirects to endsession
- [ ] Sweeper ends idle sessions but skips ones with an open ConsentRequest
- [ ] Absolute lifetime always ends a session
- [ ] Tests with a fake clock

## Verification

dotnet test --filter Sweeper

## Hand-off

## Review

- 2026-10-19 20:08 · approve · PR #85 merged into main @ 55c7502 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:56 · kanban-board · blocked → backlog · blockers done: T-009
- 2026-10-19 19:42 · scholtz · taken · status stays backlog
- 2026-10-19 19:42 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 20:08 · user · in-progress → done · PR #85 merged into main @ 55c7502 (synced by kanban-sync)
