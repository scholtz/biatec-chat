---
id: T-241
title: "Authenticator: two device keys - approval key bound to biometrics in the keystore, presence key for proofs and Deny (BCHAT-007)"
status: review
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-239, T-240, T-244]
area: ["apps/biatec_authenticator/lib/keys/**", "apps/biatec_authenticator/lib/approvals/**", "apps/biatec_authenticator/lib/security/**", "apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/android/app/src/main/kotlin/**", "apps/biatec_authenticator/ios/Runner/**", "apps/biatec_authenticator/test/**", "apps/biatec_authenticator/README.md", "packages/consent-protocol/mock-oidc/**"]
parallel: yes
conflicts_with: []
issue: "#260"
branch: "task/T-241"
owner: "session-20261114-1930"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#secure-area"
created: 2026-11-12
updated: 2026-11-14
---

# T-241 - Authenticator: two device keys (BCHAT-007)

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decision D9. Server side: ludovit-scholtz/AlgorandGoogleDriveAccount#32. Audit finding BCHAT-007 (issue 260): `audits/reports/20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01.md`, lines 426 to 454.

The device key is created without any user-authentication requirement (`android/app/src/main/kotlin/io/biatec/authenticator/KeystorePlugin.kt` lines 87 to 92; `ios/Runner/KeystorePlugin.swift` line 64 uses `.privateKeyUsage` only). The biometric check before an approval is a Dart boolean (`BiometricGate`). On a compromised phone, code running as the app can sign an approval with no prompt. The spec (Secure Area, rule 3) requires "a cryptographic binding". Binding the one existing key would make every device proof (`lib/security/device_proof.dart`, one per HTTP call), every background fetch and every Deny raise a prompt, so the protocol (T-239) splits the key in two.

## Goal

An approval can only be signed after the operating system itself verified a strong biometric for that one signature; everything else the app signs needs no prompt.

## Scope

- In:
  - **Approval key.** Android: `setUserAuthenticationRequired(true)`, `setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)` (per-use), `setInvalidatedByBiometricEnrollment(true)`, `setUnlockedDeviceRequired(true)`, StrongBox when available; signing goes through `BiometricPrompt` with a `CryptoObject` wrapping the `Signature`, so the prompt and the signature are one operation. iOS: access control `.privateKeyUsage` and `.biometryCurrentSet`, signing with an `LAContext` whose prompt text is the app's fixed string. After creation, read back `KeyInfo.isUserAuthenticationRequired` and the security level (Android) and refuse to enroll if the key is not hardware backed and authentication bound (show "This phone cannot protect approvals").
  - **Presence key.** Android: `setUnlockedDeviceRequired(true)`, no user authentication; iOS: `.privateKeyUsage` with `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`.
  - **Channel API** (`lib/keys/device_key_store.dart`): `generateKeys()` returning both public JWKs and `kid`s, `signPresence(bytes)`, `signApproval(bytes, promptTitle, promptSubtitle)` (the only call that prompts), `keysState()` (`ok`, `approvalKeyInvalidated`, `missing`).
  - **Use.** Device proofs, `deny` and `code_failed` receipts: presence key with the presence attestation. `approve` receipts, `biatec_settings_change`, `biatec_device_approval`: approval key. `approval_controller.approve()` drops the separate `gate.confirm` call for the signature: the OS prompt is the gate. The prompt subtitle names the client and the total, taken from the fetched request.
  - **Enrollment** sends `presence_kid`, `presence_public_key` and `approval_proof` (T-239 enrollment proof) and stores both attestations.
  - **Invalidation.** `KeyPermanentlyInvalidatedException` (Android) or the iOS equivalent → state `approvalKeyInvalidated`: approvals are disabled with an explanation, Deny still works, and the user is led to re-enroll (new keys, new enrollment; the old device record is cancelled or left to the server's rules).
  - **Existing installs** with a single key: on start, treat as `missing` presence key → re-enroll. No silent migration.
  - **Mock** (`mock-oidc`): accept and require the two keys behind `MOCK_TWO_KEYS=1` (default on in the Flutter contract tests), issue both attestations, refuse an approval signed by the presence key and a proof signed by the approval key.
  - Fix the stale statements in `apps/biatec_authenticator/README.md` about `local_auth` and certificate pinning.
- Out: in-app login and activation of a new device (T-242); server verification of key attestation (AlgorandGoogleDriveAccount issue 36).

## Parallel work

Parallel work possible: **yes** (after its blockers; T-244 changes the same Android and iOS projects first; T-242 waits for this task).

## Acceptance criteria

- [x] Dart tests with a fake channel: approve uses `signApproval` exactly once and nothing else prompts; deny, `code_failed` and proofs use `signPresence`; a cancelled prompt leaves the request pending and consumes no code attempt; `approvalKeyInvalidated` disables Approve and keeps Deny.
- [x] Receipts produced by the app's code verify with `packages/consent-protocol/scripts/lib.mjs` (contract test against the mock with `MOCK_TWO_KEYS=1`).
- [ ] Android instrumented test (or a documented manual run when no device is available to the agent): the approval key reports `isUserAuthenticationRequired == true`, hardware backed, and `Signature.initSign` outside a `BiometricPrompt` throws `UserNotAuthenticatedException`.
- [ ] `flutter analyze` and `flutter test` pass; the Kotlin compiles (`./gradlew :app:compileDebugKotlin`).
- [ ] Manual device checklist in the README, to be run by a person on a real Android phone and a real iPhone: enroll, approve (prompt appears once), deny (no prompt), add a fingerprint (approval key invalidated, re-enroll). The Hand-off states plainly which of these were run and which were not.

## Implementation notes

- Native code here has never run on a device (README lines 119 to 121) and the Swift is not compiled in CI. Say so in the Hand-off; do not tick a device criterion from a unit test.
- `BiometricPrompt` needs a `FragmentActivity`; check `MainActivity`.
- Never fall back to a software key or to device credential (PIN) for the approval key.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test && (cd android && ./gradlew :app:compileDebugKotlin)`

## Hand-off

PR #414, `task/T-241` @ b025d55. Local: `flutter test` 1235 pass, mock-oidc 60, the contract tests (approvals, enrollment, settings, channel) pass against a mock started with `MOCK_TWO_KEYS=1`, the device-proof test against a mock with `MOCK_DEVICE_PROOF=1`. CI: backend, js, images, authenticator-security pass; `authenticator` fails only on the Flutter SDK drift that also fails on main.

- Two keys: approval key bound to a strong biometric per use (Android `BiometricPrompt` + `CryptoObject`, invalidated on new biometric enrollment, StrongBox when available; iOS `.biometryCurrentSet`), presence key for device proofs, Deny and `code_failed`. `lib/keys/device_key_store.dart` channel API with one wire vocabulary pinned by tests (a mismatch found in review would have broken approving on a device). Enrollment sends `presence_kid`, `presence_public_key`, `approval_proof`, `presence_proof`.
- The two-key flow is behind `AppConfig.twoKeys` (on for the mock, off against real Biatec OIDC unless `--dart-define=TWO_KEY_DEVICE=true`). Single-key mode keeps old installs working (legacy keystore alias) and creates only the prompt-free key.
- NOT verified on a device or in Xcode: `BiometricPrompt` with a `CryptoObject`, invalidation handling, StrongBox, `UserNotAuthenticatedException` outside a prompt, all Swift code (never compiled), the Android instrumented test (compiles only). No device criterion is ticked; the manual checklist is in the README. `./gradlew :app:compileDebugKotlin` builds only with `-PskipDependencyChecks=true` (existing Gradle drift).
- Known limits: with the two-key flag on, the presence key requires an unlocked device, so the stay-connected service cannot sign the ticket request for a NEW hub connection while the phone is locked; tightening settings changes are unsigned (the app's gate); `keysState` on Android is cached for 5 minutes (each probe leaves a keymaster operation open); `approval_proof`/`presence_proof` field names are chosen here and documented in the mock README, not in the spec; `biatec_device_approval` signing is T-242.

**Unblocks:** T-242 (in-app login and activation of a new device).

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-239, T-240
- 2026-11-13 09:05 · scholtz · blocked → blocked · waits for T-244 too (both change the Android and iOS projects)
- 2026-11-14 16:36 · kanban-board · blocked → backlog · blockers done: T-239, T-240, T-244
- 2026-11-14 19:30 · scholtz · backlog → in-progress · claimed by session-20261114-1930 on task/T-241
- 2026-11-14 22:40 · scholtz · in-progress → review · PR #414 @ b025d55
