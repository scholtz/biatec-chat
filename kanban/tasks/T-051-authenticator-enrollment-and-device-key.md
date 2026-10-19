---
id: T-051
title: Authenticator enrollment and device key
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-047, T-050]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/keys/**"]
issue: "#52"
branch: ""
owner: ""
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
