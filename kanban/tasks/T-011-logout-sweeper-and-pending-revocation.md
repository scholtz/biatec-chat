---
id: T-011
title: Logout, sweeper and pending revocation
status: blocked
priority: P1
epic: "Sessions & auth"
blocked_by: [T-009]
area: ["backend/Biatec.Chat.Api/Auth/Logout/**", "backend/Biatec.Chat.Api/Sweeper/**"]
branch: ""
owner: ""
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

## Acceptance criteria

- [ ] Logout clears cookie and vault entry and redirects to endsession
- [ ] Sweeper ends idle sessions but skips ones with an open ConsentRequest
- [ ] Absolute lifetime always ends a session
- [ ] Tests with a fake clock

## Verification

dotnet test --filter Sweeper

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
