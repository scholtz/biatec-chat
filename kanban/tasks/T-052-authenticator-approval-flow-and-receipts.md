---
id: T-052
title: Authenticator approval flow and receipts
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-051]
area: ["apps/biatec_authenticator/lib/approvals/**"]
issue: "#53"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#protocol-rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-052 · Authenticator approval flow and receipts

## Context

Card rendered only from fetched authorization_details; user types binding code (3 attempts); biometric; device key signs canonical receipt; deny is signed too; offline refuses approval.

## Goal

Phone approval that the backend consent flow (T-049) completes with.

## Scope

- In: Pending list fetch, card, code entry, biometric gate, JCS receipt signing, post to mock, deny, history.
- Out: Push (T-053).

## Acceptance criteria

- [ ] Approve flow produces a receipt the mock accepts
- [ ] Third wrong code yields code_failed
- [ ] Offline mode cannot approve
- [ ] Card fields match authorization_details

## Verification

flutter test; manual run with mock + backend

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
