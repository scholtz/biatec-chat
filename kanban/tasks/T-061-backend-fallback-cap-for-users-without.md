---
id: T-061
title: Backend fallback cap for users without an active device
status: blocked
priority: P3
epic: "Consent & authenticator"
blocked_by: [T-025, T-049]
area: ["backend/Biatec.Chat.Tools/Caps/NoDevice/**"]
parallel: yes
conflicts_with: []
issue: "#62"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#required-changes-to-biatec-oidc"
created: 2026-10-19
updated: 2026-10-19
---

# T-061 · Backend fallback cap for users without an active device

## Context

Required Changes row "Reduced per-client daily cap for users without an active device" (protocol rule 4) has the
fallback: "When the user has no active device, enforce the reduced 100 EUR per-client daily cap in the backend for
such users (weaker because it is backend-side), and block above-threshold signing for them." Whether the user has an
active device comes from `GET /consent/settings` (mock in T-047, client in T-049).

## Goal

Implement the rule 4 fallback until Biatec OIDC enforces it.

## Scope

- In: Daily per-tenant 100 EUR (configurable) cap for users without an active device, reusing the T-025 reservation
  store; refusal of above-threshold signing with a message that offers enrollment; a config switch to turn it off
  once Biatec OIDC ships the real rule.
- Out: The OIDC-side implementation (record it in `docs/required-changes-biatec-oidc.md`, T-044).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] No-device user is refused once the day's total would exceed the cap
- [ ] Above-threshold action for a no-device user is refused with an enrollment hint
- [ ] Users with an active device are unaffected
- [ ] Tests with fake consent settings

## Verification

dotnet test --filter NoDevice

## Hand-off

## Review

## Activity log

- 2026-10-19 18:00 · coordinator · (new) → blocked · created in task review: rule 4 fallback had no task
