---
id: T-022
title: Confirm, sign at Biatec OIDC and submit
status: blocked
priority: P0
epic: "Wallet tools"
blocked_by: [T-010, T-019, T-021]
area: ["backend/Biatec.Chat.Tools/Signing/**", "backend/Biatec.Chat.Api/Chat/Confirm/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-19
updated: 2026-10-19
---

# T-022 · Confirm, sign at Biatec OIDC and submit

## Context

Chain is build (MCP) -> preview -> sign at Biatec OIDC POST /wallet/{network}/{address}/sign -> submit (MCP or algod); one sender per call; step-up 401 surfaced as consent pending.

## Goal

The confirm path every fund-moving capability shares.

## Scope

- In: Confirm endpoint (CSRF), scope check, sign call with the exact previewed bytes, handling of 401 insufficient_user_authentication (store ConsentRequest, emit consent status; completion in T-049); Biatec OIDC unreachable yields a clear "sign-in service unavailable" message without changing the tier or vault, recorded as an outage event, submit via MCP submitTransactionToBlockchain or algod, confirmation tracking, txIds on ActionStep, receipt event.
- Out: Consent completion (T-049).

## Acceptance criteria

- [ ] Signed bytes come only from OIDC; MCP signTransaction never used
- [ ] Submitted bytes equal previewed group (hash check)
- [ ] Step-up response produces a consent-pending event
- [ ] OIDC outage returns "sign-in service unavailable" and keeps the session Authenticated
- [ ] Tests with fake OIDC and MCP

## Verification

dotnet test --filter Signing

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · fixed consent-completion reference (T-041 → T-049); added OIDC-outage behaviour from Non-Functional Requirements
