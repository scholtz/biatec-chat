---
id: T-049
title: Backend consent orchestrator
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-022, T-047]
area: ["backend/Biatec.Chat.Tools/Consent/**"]
branch: ""
owner: ""
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
