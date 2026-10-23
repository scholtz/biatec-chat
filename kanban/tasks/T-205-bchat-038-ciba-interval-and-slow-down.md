---
id: T-205
title: "Honour the CIBA interval and back off on slow_down (BCHAT-038)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Consent/**", "backend/Biatec.Chat.Api/Chat/Confirm/ConfirmService.cs", "backend/Biatec.Chat.Core/Data/**", "backend/Biatec.Chat.Api/Migrations/**", "backend/Biatec.Chat.Tests/Consent/**"]
parallel: yes
conflicts_with: []
issue: "#291"
branch: "task/T-205"
owner: "session-20261023-1252"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-205 - Honour the CIBA interval and back off on slow_down (BCHAT-038)

## Context

Security audit finding BCHAT-038 (Low, issue #291, FIN-5, RISK-006). The consent stream polls the CIBA token endpoint every fixed 2 seconds. The `interval` Biatec OIDC returns with the step-up is read but not kept, and a `slow_down` answer is treated like `authorization_pending`, so a server that asks for a slower rate is polled just as fast (RFC 8628 3.5: add 5 seconds on slow_down; CIBA 10.2 uses the same rule).

## Goal

Polling waits at least the interval Biatec OIDC gave, and at least 5 seconds more after every `slow_down`.

## Scope

- In: the interval stored on the consent request (migration), `slow_down` raising it by 5 s (capped), the stream waiting `max(configured, requested)`; tests.
- Out: the polling transport and the widget.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] The consent request keeps the interval from the step-up (clamped to 1 to 30 s).
- [x] A `slow_down` poll answer adds 5 seconds to the stored interval (cap 60 s), and the stream's next wait is at least the stored interval.
- [x] Authorization pending keeps the interval; existing consent tests stay green.

## Verification

`dotnet test backend --filter Consent`, full suite in CI.

## Hand-off

- **Summary:** `ConsentRequest.PollIntervalSeconds` (kept from the step-up `interval`, clamped 1 to 30; rows from before the migration get the CIBA default 5) and `LastPolledAt` (migration `ConsentPollInterval`). The orchestrator enforces the interval itself: a poll sooner than the interval after the last one never reaches the token endpoint (one caller wins the slot atomically, the rest get the pending status and when to come back), so a reconnected stream, a second tab or the plain poll endpoint cannot poll faster. `slow_down` raises the stored interval atomically by 5 s (cap 60 s). `ConsentAdvance.PollAfter` carries the wait and the SSE stream waits `max(Consent:Interval, PollAfter)`.
- **Verification:** `dotnet test --filter Consent|Confirm`: 187 pass, with new tests for the kept interval, the +5 s step and the cap, and for early polls not reaching Biatec OIDC. Full suite in CI.

## Review

## Activity log

- 2026-10-23 12:52 · kanban-split · (new) → backlog · created from audit issue #291
- 2026-10-23 12:52 · scholtz · taken · status stays backlog
- 2026-10-23 12:52 · session-20261023-1252 · backlog → in-progress · claimed, branch task/T-205
