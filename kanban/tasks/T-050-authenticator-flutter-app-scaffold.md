---
id: T-050
title: Authenticator Flutter app scaffold
status: backlog
priority: P2
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-19
updated: 2026-10-19
---

# T-050 · Authenticator Flutter app scaffold

## Context

Screens: enrollment, pending approvals, approval card, history, device management, trusted clients and thresholds, security settings.

## Goal

Runnable app shell for later flows.

## Scope

- In: flutter create, navigation, empty screens, config for OIDC base URL (mock by default), theming with large-type support.
- Out: Enrollment and approval logic.

## Acceptance criteria

- [ ] flutter analyze clean
- [ ] flutter test passes
- [ ] App runs on Android emulator with all screens reachable
- [ ] Config points to mock OIDC

## Verification

cd apps/biatec_authenticator && flutter analyze && flutter test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
