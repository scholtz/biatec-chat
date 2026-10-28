---
id: T-225
title: "Authenticator: navigation with Codes first, Trusted clients inside Settings"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214]
area: ["apps/biatec_authenticator/lib/shell/shell_controller.dart", "apps/biatec_authenticator/lib/shell/app_shell.dart", "apps/biatec_authenticator/lib/security/security_screen.dart", "apps/biatec_authenticator/test/shell/app_shell_test.dart", "apps/biatec_authenticator/test/settings/settings_screens_test.dart"]
parallel: no
conflicts_with: [T-220]
issue: "#362"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-225 - Authenticator: navigation with Codes first, Trusted clients inside Settings

## Context

T-214 built a shell with Approvals, History, Devices, Clients and Settings. The spec now has Codes, Approvals, History, Devices, Settings, with Trusted clients as a screen inside Settings under Security. Five destinations is the most a bottom bar should have, so Clients moves.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

The bar shows Codes, Approvals, History, Devices and Settings, Codes is the first destination, and Trusted clients is reachable from Settings.

## Scope

- In: `AppTab` gets `codes` (first) and loses `clients`; the Codes tab root is a placeholder screen with an empty state until T-224 replaces it; Settings gets a row 'Trusted clients and thresholds' that opens `ClientsScreen` inside the Settings tab; the default tab after unlock is Approvals when a request is pending and Codes otherwise; tests and badge logic follow.
- Out: the Codes content (T-224), redesign of Clients (T-218).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-220: they change the same files.

## Acceptance criteria

- [ ] The bar and rail show the five destinations in the spec order, each with an icon, label and tooltip.
- [ ] Trusted clients opens from Settings and its back button returns to Settings; the screen is unchanged otherwise.
- [ ] Push taps and the in-app notice still go to Approvals; the badge stays on Approvals.
- [ ] The initial tab is Approvals when a request is pending, otherwise Codes.
- [ ] `flutter analyze` is clean and `flutter test` passes in `apps/biatec_authenticator`, with new tests for every behaviour above (the failing case first for each security rule).

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Use platform cryptography, not your own. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; for native work also build the platform that changed (`flutter build windows` proves only the Dart side; say in the hand-off what could not be run on a device).

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-28 09:00 · claude · (new) → blocked · created from the request to add two-factor codes, a biometric lock and industry-standard security
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-214
