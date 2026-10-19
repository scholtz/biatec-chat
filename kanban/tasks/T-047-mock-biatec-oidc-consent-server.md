---
id: T-047
title: Mock Biatec OIDC consent server
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-046]
area: ["packages/consent-protocol/mock-oidc/**"]
parallel: yes
conflicts_with: []
issue: "#48"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#two-entry-points-one-request-model"
created: 2026-10-19
updated: 2026-10-19
---

# T-047 · Mock Biatec OIDC consent server

## Context

Conformance suite runs against a mock Biatec OIDC; we cannot change the real one, so the mock implements backchannel auth, step-up on sign, CIBA token grant with consent_receipt, device enrollment and receipt verification.

## Goal

A local OIDC stand-in for consent, used by backend tests, authenticator and conformance.

## Scope

- In: Node TS server implementing the endpoints and rules needed (binding code, 3 attempts, 300 s validity, single use, group-hash binding), seeded test users and clients, Dockerfile.
- Out: Real identity providers.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Step-up on sign returns 401 with auth_req_id and binding code
- [ ] CIBA token returned after valid receipt
- [ ] Mismatching group hash refused
- [ ] Tests cover rules 2, 5, 9

## Verification

pnpm --filter mock-oidc test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · P2 → P1: on the critical path of the P1 demo test (T-059)
- 2026-10-19 19:30 · kanban-board · blocked → backlog · blockers done: T-046
