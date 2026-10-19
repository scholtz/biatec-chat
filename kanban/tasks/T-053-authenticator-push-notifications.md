---
id: T-053
title: Authenticator push notifications
status: blocked
priority: P3
epic: "Consent & authenticator"
blocked_by: [T-052]
area: ["apps/biatec_authenticator/lib/push/**"]
parallel: yes
conflicts_with: []
issue: "#54"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-19
updated: 2026-10-19
---

# T-053 · Authenticator push notifications

## Context

FCM/APNs carrying only a request id; content always fetched from OIDC.

## Goal

Approvals arrive without manual refresh.

## Scope

- In: Push plugin setup, token registration with mock, handler opening the pending request.
- Out: Production push credentials.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Push payload contains only request id
- [ ] Tapping opens the right request
- [ ] Works on Android emulator with test FCM
- [ ] No secrets committed

## Verification

manual emulator test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
