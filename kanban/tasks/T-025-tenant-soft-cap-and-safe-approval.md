---
id: T-025
title: Tenant soft cap and safe approval caps
status: blocked
priority: P2
epic: "Wallet tools"
blocked_by: [T-022]
area: ["backend/Biatec.Chat.Tools/Caps/**"]
issue: "#26"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-025 · Tenant soft cap and safe approval caps

## Context

Rolling-window soft cap (default 1500 EUR/h, floor = consent ceiling) with atomic reservation; above it set requireConsent; safe approvals capped 5/h and 2000 EUR/h, executions 10/h.

## Goal

Damage limiter against a hostile host page.

## Scope

- In: SoftCapSpend reservations and finalization, release on expiry/cancel/deny/fail/step-up, SafeApprovalCount caps, requireConsent flag on sign requests, pruning.
- Out: OIDC-side enforcement.

## Acceptance criteria

- [ ] Concurrent reservations cannot exceed the cap
- [ ] Release paths tested
- [ ] requireConsent set while window total exceeds cap
- [ ] Safe caps enforced per kind

## Verification

dotnet test --filter Caps

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
