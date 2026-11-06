---
id: T-234
title: "Authenticator: send the platform attestation at enrollment"
status: done
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-230]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/integrity/attestation.dart", "apps/biatec_authenticator/android/app/build.gradle.kts", "apps/biatec_authenticator/android/app/src/main/**/Attestation*", "apps/biatec_authenticator/test/enrollment/**", "docs/required-changes-biatec-oidc.md"]
parallel: no
conflicts_with: [T-220, T-232]
issue: "#386"
branch: "task/T-234"
owner: "session-20261106-0900"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-05
updated: 2026-11-06
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

Implemented in PR #391, branch task/T-234 (squash merged).

- Enrollment: `AttestationProvider.supported()`, `OidcEnrollmentClient.requestChallenge` and `refreshAttestation`, `EnrollmentController(attestation:, attestationTimeout:)`; the record keeps `attestationRefreshAfter` (`copyWith`). A refresh runs after that date at the next `refresh()`, with an hour's wait after a failure. Without support, a token, a challenge or time, the enrollment goes without an attestation.
- Android: `AttestationChannel.kt` (Play Integrity), manifest placeholder `playIntegrityProject` from `-PplayIntegrityProject=<number>`, validated numeric in the build. iOS: App Attest from T-228, now with `supported`.
- Mock Biatec OIDC: `POST /devices/challenge`, the two fields on `POST /devices`, `PUT /devices/attestation`. API shape and fallback: row 25 of `docs/required-changes-biatec-oidc.md`.
- Matrix: RESILIENCE-1 and -2 stay `partly` until Biatec OIDC verifies the tokens (O-8) and the native code has run on a device (O-1). Release checklist: set `playIntegrityProject`.
- Not run on a device. Tests: flutter 1029 pass, mock-oidc 34 pass. One review round, user capped and merged.

**Unblocks:** none.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

- 2026-11-06 22:26 · approve · PR #391 merged into main @ a45c860 (recorded by kanban-sync)

## Activity log

- 2026-11-05 10:20 · claude · (new) → backlog · created from the MASVS matrix of T-230 (open item)
- 2026-11-05 10:25 · claude · ordering and file overlap decided: blocked_by T-230 (matrix row), conflicts_with T-220 and T-232
- 2026-11-05 10:04 · kanban-board · backlog → blocked · waiting on: T-230
- 2026-11-05 11:27 · kanban-board · blocked → backlog · blockers done: T-230
- 2026-11-06 09:00 · scholtz · backlog → in-progress · claimed by session-20261106-0900 on task/T-234
- 2026-11-06 22:26 · user · in-progress → done · PR #391 merged into main @ a45c860 (synced by kanban-sync)
