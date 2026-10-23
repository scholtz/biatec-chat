---
id: T-209
title: "Mock OIDC: model interval and slow_down, cap open requests, scope receipts to the subject, deny an inactive device (BCHAT-024)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/consent-protocol/mock-oidc/**", "packages/consent-protocol/conformance/**", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#277"
branch: "task/T-209"
owner: "session-20261023-1341"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-209 - Mock OIDC: model interval and slow_down, cap open requests, scope receipts to the subject, deny an inactive device (BCHAT-024)

## Context

Security audit finding BCHAT-024 (Low, issue #277, CNS-2/3/4, RISK-022). The mock Biatec OIDC lets non-conforming clients pass: the token endpoint ignores `interval`/`slow_down` and any number of requests can stay open; a receipt can be fetched by any bearer of the owning client, not only for the subject it was issued to; a device that is still inactive (rule 7, 24 hours) can change settings (tightening) and cancel other enrollments.

## Goal

The mock enforces the polling interval (with +5 s on slow_down), a cap on open requests per user, the subject rule for receipt retrieval and the active-device rule for settings and cancellation, with tests, and the conformance suite still passes.

## Scope

- In: the four rules in the mock, their tests, the subject rule written into docs/required-changes-biatec-oidc.md (row 4).
- Out: the real Biatec OIDC.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] A token poll sooner than the request's interval (2 s) is answered `slow_down` and raises that request's interval by 5 s; a poll at or after the interval is judged on the request state.
- [x] More than 20 open requests of one user are refused (429).
- [x] A receipt is returned only to a bearer whose subject owns the request.
- [x] An inactive device can neither change settings nor cancel another enrollment.
- [x] Mock and conformance suites pass; docs row updated.

## Verification

`pnpm --filter mock-oidc test`, `pnpm --filter @biatec/consent-protocol conformance`.

## Hand-off

- **Summary:** the mock (`mock-oidc/src/app.ts`) now models: `interval` (2 s) and `slow_down` (+5 s on the request's interval; only a pending request is judged on timing, an approved one is returned on the first poll), at most 20 open requests per user (429 `too_many_requests`; 20 because the shared test mock leaves requests open; the real value is for the OIDC owners), receipts only to a bearer whose subject owns the request (the client by its credentials still can), and an inactive device (rule 7) neither changes settings (tightening included) nor cancels enrollments. The conformance kit waits the interval and adds 5 s after a slow_down. docs/required-changes-biatec-oidc.md row 4 states the rules for the real server.
- **Decision for the user:** the audit lists "inactive device can tighten settings" as a gap, so tightening now needs an active device too; the earlier test (T-084) said "tightening is fine from any device" and was changed accordingly.
- **Verification:** `pnpm --filter mock-oidc test`: 30 pass (4 new, 1 rewritten); typecheck clean; conformance: 23 pass.

## Review

- 2026-10-23 13:51 · approve · PR #336 merged into main @ 1885c45 (recorded by kanban-sync)

## Activity log

- 2026-10-23 13:41 · kanban-split · (new) → backlog · created from audit issue #277
- 2026-10-23 13:41 · scholtz · taken · status stays backlog
- 2026-10-23 13:41 · session-20261023-1341 · backlog → in-progress · claimed, branch task/T-209
- 2026-10-23 13:51 · user · in-progress → done · PR #336 merged into main @ 1885c45 (synced by kanban-sync)
