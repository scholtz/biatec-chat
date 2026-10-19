---
id: T-049
title: Backend consent orchestrator
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-022, T-047]
area: ["backend/Biatec.Chat.Tools/Consent/**"]
parallel: no
conflicts_with: [T-024, T-025, T-035, T-057]
issue: "#50"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#flow-1"
created: 2026-10-19
updated: 2026-10-19
---

# T-049 · Backend consent orchestrator

## Context

On 401 insufficient_user_authentication the backend shows the binding code, polls the CIBA token endpoint, retries signing with the elevated token, stores the receipt verbatim; sends maxConsentSeconds; predicts consent via /consent/settings or says may need approval.

## Goal

Phone approval completes the confirm path.

## Scope

- In: ConsentRequest lifecycle, CIBA polling, retry signing, consent status SSE, receipt into evidence, prediction fallback text, configurable OIDC base URL so tests use the mock.
- Out: Real OIDC support (handoff in T-044).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-024, T-025, T-035, T-057: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] End-to-end against mock: challenge, approve, signed
- [ ] Deny cancels the action in the widget
- [ ] Receipt stored and recorded in evidence
- [ ] Expired consent leads to rebuilt preview

## Verification

dotnet test --filter Consent

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · P2 → P1: on the critical path of the P1 demo test (T-059)
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-047
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped T-014 (done)
