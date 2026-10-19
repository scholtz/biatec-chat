---
id: T-051
title: Authenticator enrollment and device key
status: in-progress
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-047, T-050]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/keys/**"]
parallel: yes
conflicts_with: []
issue: "#52"
branch: "task/T-051"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#protocol-rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-051 · Authenticator enrollment and device key

## Context

Hardware-backed key (Secure Enclave / StrongBox), attestation at enrollment, 24 h inactive delay unless approved by an existing device, recovery code shown once after activation.

## Goal

Device enrolled against the mock OIDC.

## Scope

- In: Key generation via platform keystore plugin, enrollment calls to mock, activation state, recovery code screen.
- Out: Real App Attest / Play Integrity verification (stubbed in mock).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Key is non-exportable on device
- [ ] Enrollment against mock succeeds
- [ ] Inactive state shown during delay
- [ ] Tests for state machine

## Verification

flutter test; manual emulator run

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:38 · kanban-board · blocked → backlog · blockers done: T-047, T-050
- 2026-10-19 20:38 · scholtz · taken · status stays backlog
- 2026-10-19 20:38 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
