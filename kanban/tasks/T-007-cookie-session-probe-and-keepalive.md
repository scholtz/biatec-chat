---
id: T-007
title: Cookie session, probe and keepalive
status: blocked
priority: P0
epic: "Sessions & auth"
blocked_by: [T-006]
area: ["backend/Biatec.Chat.Api/Sessions/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-19
updated: 2026-10-19
---

# T-007 · Cookie session, probe and keepalive

## Context

__Host- cookie, HttpOnly, Secure, SameSite=Lax, Path=/; 15 min idle, 8 h absolute; widget probes with two credentialed requests to learn if the cookie came back; keepalive extends idle only.

## Goal

Server-side sessions with the cookie probe the widget needs.

## Scope

- In: ChatSession creation on first session call, probe endpoint semantics (first sets cookie, second reports cookie seen), keepalive endpoint, idle/absolute expiry checks, session state endpoint (tier, email, primary address, popup request status).
- Out: Login (T-009), sweeper (T-011).

## Acceptance criteria

- [ ] Cookie attributes exactly as specified
- [ ] Probe reports cookieReturned true on second call with cookie, false without
- [ ] Keepalive extends idle but never absolute expiry
- [ ] Expired session is treated as new anonymous session

## Verification

dotnet test --filter Sessions; curl -c/-b sequence documented in hand-off

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
