---
id: T-224
title: "Authenticator: Codes tab (two-factor codes list, add and edit)"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-221, T-222, T-223, T-225, T-227, T-229]
area: ["apps/biatec_authenticator/lib/totp/ui/**", "apps/biatec_authenticator/lib/codes/**", "apps/biatec_authenticator/test/codes/**", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/ios/Runner/Info.plist"]
parallel: yes
conflicts_with: []
issue: "#361"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-224 - Authenticator: Codes tab (two-factor codes list, add and edit)

## Context

Spec: "Two-Factor Codes". The Codes tab is the first destination of the app. It is built on the engine (T-221), the vault (T-223), the lock (T-222) and the navigation (T-225).

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

A user can add accounts by QR, link or key and read, copy and manage their codes, as in the best authenticator apps.

## Scope

- In: the Codes tab with the list (issuer, label, grouped code, countdown ring plus seconds in text, next code in the last 5 s, tap to copy, HOTP counter button, search, reorder, rename, delete with fresh biometric and confirmation, optional tap-to-reveal); add flow (QR scanner with a camera permission asked only then and no image stored, paste an otpauth link, manual key entry) with preview and validation messages; duplicate handling; empty state with the three ways to add; copy marks the clipboard sensitive and clears it after 30 s when unchanged; the clock check against the `Date` header of Biatec OIDC calls with a warning above 5 s; one shared ticker for all countdowns that stops when the tab is hidden or the app is locked; a QR scanner dependency (`mobile_scanner` or equivalent maintained package) with its camera permission strings.
- Out: backup and restore (T-226).

## Parallel work

Ordering: T-227, T-229 change files this task also edits (MainActivity, the manifests, Info.plist or pubspec.yaml), so they go first.

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Adding by QR, by link and by manual key each produces the same account; invalid input shows the engine's specific message.
- [ ] The list updates codes at period boundaries with no drift (a test with an injected clock), shows the countdown as text as well as a ring, and previews the next code in the last 5 s.
- [ ] Copy puts the code on the clipboard marked sensitive and clears it after 30 s only if the clipboard still holds that code.
- [ ] Delete asks for a fresh biometric and a confirmation; reorder and rename persist.
- [ ] A screen reader announces issuer, label and the code digit by digit in groups, the countdown at 10 s, 5 s and at renewal, and not every second; layouts hold at 3x text in light and dark mode.
- [ ] Locking the app clears the displayed codes from the widget tree and the ticker stops.
- [ ] A device clock off by more than 5 s shows a warning that explains the fix.
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
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-221, T-222, T-223, T-225
