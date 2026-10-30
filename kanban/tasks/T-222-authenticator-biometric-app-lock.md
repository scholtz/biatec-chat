---
id: T-222
title: "Authenticator: biometric-only lock for the whole secure area"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214, T-227]
area: ["apps/biatec_authenticator/lib/lock/**", "apps/biatec_authenticator/lib/shell/app_gate.dart", "apps/biatec_authenticator/lib/shell/app_lock_gate.dart", "apps/biatec_authenticator/lib/security/biometric_policy.dart", "apps/biatec_authenticator/test/lock/**", "apps/biatec_authenticator/android/app/src/main/**/BiometricChannel*", "apps/biatec_authenticator/ios/Runner/BiometricChannel*"]
parallel: no
conflicts_with: [T-220]
issue: "#359"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-30
---

# T-222 - Authenticator: biometric-only lock for the whole secure area

## Context

Spec: "Secure Area And Biometric Lock". Today a biometric check happens only before an approval, through `local_auth`, and the user may turn it off. The whole app, not just approvals, must open only with a fingerprint or face, with no other way in. Read the spec section first; it is the requirement.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

Nothing in the secure area is reachable until a strong biometric succeeds, and the app locks again on the rules in the spec.

## Scope

- In: a `LockController` (locked, unlocking, unlocked, noBiometric, biometricsChanged, compromised placeholder for T-228) and an `AppLockGate` between the app and the shell; strong-biometric-only authentication (Android `BIOMETRIC_STRONG`, iOS biometry-only policy, `biometricOnly: true` with no device-credential fallback); the lock screen (branded, nothing sensitive, one button); the 'no biometric enrolled' screen; the lock timing (launch, background delay 0, 30 s, 1 min or 5 min with default 30 s and no 'never', screen lock, 2 minutes idle) with the setting stored locally; an idle detector on pointer events; clearing in-memory secrets through a `LockListener` hook on lock; a tapped notification keeps only the request id in memory until unlock; a fresh biometric for approval, delete, backup and settings change through `LockController.reauthenticate(reason)`; the approvals biometric requirement can no longer be turned off in the app.
- Out: the hardware key binding and invalidation on biometric change (T-223 and the existing key store), screenshot blocking (T-227), integrity (T-228), the Codes tab (T-224).

## Parallel work

Ordering: T-227 change files this task also edits (MainActivity, the manifests, Info.plist or pubspec.yaml), so they go first.

Parallel work possible: **no**. Don't run it at the same time as T-220: they change the same files.

## Acceptance criteria

- [ ] A fake biometric that fails, is cancelled, is locked out, or reports no hardware or no enrollment leaves the secure area locked; none of them opens it.
- [ ] No code path unlocks without a biometric success: no PIN, no fallback, no debug flag in release (a test greps that the policy never sets device-credential fallback).
- [ ] The lock engages at launch, after the background delay for each of the four settings, on screen lock, and after 2 minutes idle; each has a test with an injected clock and lifecycle events.
- [ ] A tapped notification on a locked app shows the lock screen, and the card opens after unlock; the request id is not persisted.
- [ ] While locked the widget tree contains no secure-area screen (a test pumps the locked app and finds none), and `reauthenticate` is called by approve and by the settings changes.
- [ ] The 'no biometric' and 'biometrics changed' screens exist and say what to do.
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
- 2026-10-28 09:10 · scholtz · taken · status stays blocked
- 2026-10-30 16:40 · kanban-board · blocked → backlog · blockers done: T-214, T-227
