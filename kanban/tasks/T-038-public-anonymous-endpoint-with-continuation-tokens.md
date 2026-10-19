---
id: T-038
title: Public anonymous endpoint with continuation tokens
status: done
priority: P1
epic: "Sessions & auth"
blocked_by: [T-015]
area: ["backend/Biatec.Chat.Api/Public/**"]
parallel: no
conflicts_with: [T-009, T-022, T-032, T-036, T-060]
issue: "#39"
branch: "task/T-038"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-19
updated: 2026-10-20
---

# T-038 · Public anonymous endpoint with continuation tokens

## Context

POST /public/chat allows any origin, no cookie; 128-bit capability stored as hash; single-use continuation token per reply; one replay of a lost reply; registered-origin check; rate limits by salted IP hash; adoption into cookie session retires the identifier; idle end after 30 min.

## Goal

Anonymous chat on any page, and continuity when the cookie transport becomes available.

## Scope

- In: Endpoint, PublicConversationState, rate limiting in Redis, origin-registered query, adoption endpoint on cookie transport, refusals with distinct codes.
- Out: Ingress-level config.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-009, T-022, T-032, T-036, T-060: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Reply requires the latest continuation token
- [ ] Lost reply replayed once
- [ ] Adopted or authenticated conversation refused on public endpoint
- [ ] Rate limit triggers and raw IP never logged

## Verification

dotnet test --filter Public

## Hand-off

## Review

- 2026-10-20 00:55 · approve · PR #115 merged into main @ 83e6526 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-015
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 00:55 · user · in-progress → done · PR #115 merged into main @ 83e6526 (synced by kanban-sync)
