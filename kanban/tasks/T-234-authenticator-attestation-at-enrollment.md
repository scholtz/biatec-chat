---
id: T-234
title: "Authenticator: send the platform attestation at enrollment"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-230]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/integrity/attestation.dart", "apps/biatec_authenticator/android/app/build.gradle.kts", "apps/biatec_authenticator/android/app/src/main/**/Attestation*", "apps/biatec_authenticator/test/enrollment/**", "docs/required-changes-biatec-oidc.md"]
parallel: no
conflicts_with: [T-220, T-232]
issue: "#386"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-05
updated: 2026-11-05
---

# T-234 - Authenticator: send the platform attestation at enrollment

## Context

From T-228: the `AttestationProvider` exists (App Attest on iOS, a not-available answer on Android) but enrollment does not call it. The spec says attestation is collected at enrollment and refreshed, and verified by Biatec OIDC. Read `lib/integrity/attestation.dart`, `lib/enrollment/` and `docs/required-changes-biatec-oidc.md`.

## Goal

Enrollment sends a Play Integrity token or an App Attest attestation bound to the server challenge, and the mock verifies the shape.

## Scope

- In: ask Biatec OIDC (the mock) for a challenge nonce at enrollment, call `AttestationProvider.token(nonce)`, and send the result with the enrollment request (the fallback when the platform cannot attest: send none, and Biatec OIDC decides); the Android implementation with the Play Integrity dependency and the cloud project number from the build configuration; refresh the attestation when it is older than the server says; document the API shape in `docs/required-changes-biatec-oidc.md` (fallback: enrollment without attestation, device flagged); mock support and tests.
- Out: server-side verification in the real Biatec OIDC.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-220 (enrollment files) or T-232 (`android/app/build.gradle.kts`).

Ordering: T-230 writes `docs/AUTHENTICATOR-SECURITY.md`, whose row this task updates, so it goes first.

## Acceptance criteria

- [ ] Enrollment sends the attestation bound to the nonce when the provider has one, and sends none (not a fake) when it has not (tests).
- [ ] The Android provider builds with the Play Integrity dependency and reads the project number from configuration; without it, it answers not available.
- [ ] The required-changes document lists the challenge and attestation fields, with the fallback.
- [ ] `docs/AUTHENTICATOR-SECURITY.md` row MASVS-RESILIENCE-1 notes attestation as wired.
- [ ] `flutter analyze` is clean and `flutter test` passes.

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; say in the hand-off what could not be run on a device.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-11-05 10:20 · claude · (new) → backlog · created from the MASVS matrix of T-230 (open item)
- 2026-11-05 10:25 · claude · ordering and file overlap decided: blocked_by T-230 (matrix row), conflicts_with T-220 and T-232
- 2026-11-05 10:04 · kanban-board · backlog → blocked · waiting on: T-230
- 2026-11-05 11:27 · kanban-board · blocked → backlog · blockers done: T-230
