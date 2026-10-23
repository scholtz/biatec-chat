---
id: T-148
title: "Report the real security level of the authenticator's device key and refuse a software key (BCHAT-032)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/lib/keys/**", "apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/android/app/src/main/kotlin/**", "apps/biatec_authenticator/ios/Runner/KeystorePlugin.swift", "apps/biatec_authenticator/test/keys/**", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/test/approvals/support.dart", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#285"
branch: "task/T-148"
owner: "session-20261023-1714"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-148 - Report the real security level of the authenticator's device key and refuse a software key (BCHAT-032)

## Context

Security audit finding BCHAT-032 (Low, issue #285, RISK-003). The Android plugin asks for StrongBox and silently falls back to the TEE, and
the enrollment screen says the key is "created in secure hardware" whatever the device did. Nothing checks `KeyInfo`, so a key that ended up in
software would be enrolled and described as hardware-backed.

## Goal

The plugins report where the key lives, enrollment refuses a key that is not in hardware, and the app shows the level it got.

## Scope

- In: `KeystorePlugin.kt` reports `securityLevel` (`strongbox`, `tee`, `hardware` below API 31, `software`, `unknown`) from `KeyInfo` for `generate` and `load` and
  refuses to keep a software key; `KeystorePlugin.swift` reports `secure_enclave` after checking the key's token attribute; a `KeySecurityLevel` type in Dart,
  `DeviceKey.securityLevel`, enrollment that refuses `software` and `unknown`, and the level shown on the enrollment screen; tests with a fake channel and fake key stores.
- Out: binding the key to user authentication (BCHAT-007, needs a device to verify); attestation of the key to Biatec OIDC; running on a device.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Both plugins return `securityLevel` with `generate` and `load`; Android refuses to keep a key whose level is software (the key is deleted and `insecure_key` is returned).
- [x] `PlatformDeviceKeyStore` parses the level (a missing or unknown value is `unknown`) and `EnrollmentController.enroll` refuses `software` and `unknown`, deleting the key.
- [x] The enrollment screen no longer claims secure hardware unconditionally: it shows the level the key got.
- [x] `flutter test`, `flutter analyze` and `flutter build apk --debug` pass.

## Verification

`flutter test`, `flutter analyze`, `flutter build apk --debug` in `apps/biatec_authenticator`.

## Hand-off

- **Summary:** generate and load of both keystore plugins now return securityLevel. Android reads KeyInfo (SECURITY_LEVEL_* on API 31+, isInsideSecureHardware below) and maps it to strongbox, tee, hardware, software or unknown, and deletes a key that ended up in software, answering insecure_key. iOS reads the key's token attribute and reports secure_enclave or software. In Dart, KeySecurityLevel parses the value (anything missing or unrecognised is unknown), DeviceKey carries it, EnrollmentController.enroll refuses software and unknown (deleting the key, never calling the server) and keeps the level for display, and the enrollment screen shows 'Key protection: ...' instead of claiming secure hardware unconditionally.
- **Files changed:** apps/biatec_authenticator/lib/keys/key_security_level.dart (new), lib/keys/device_key_store.dart, lib/enrollment/enrollment_controller.dart, lib/enrollment/enrollment_screen.dart, android/app/src/main/kotlin/io/biatec/authenticator/KeystorePlugin.kt, ios/Runner/KeystorePlugin.swift, test/keys/device_key_store_test.dart (new), test/enrollment/enrollment_test.dart, test/enrollment/mock_oidc_contract_test.dart, README.md.
- **How it was verified:** flutter test 157 passed (new: level parsing and fail-closed defaults, a software or unknown key is refused and removed without a server call, each hardware level is enrolled and shown, the level is read back after a restart); flutter analyze clean; flutter build apk --debug compiles the Kotlin changes.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The Swift change (SecKeyCopyAttributes token check) is not compiled, and nothing ran on a device, so KeyInfo's real answers per device are unseen; an Android device that reports 'unknown' (KeyInfo unreadable) is refused at enrollment by design. Existing enrollments are not re-checked: the level is only displayed for them. A key already in software that was enrolled earlier (impossible with the previous Android plugin only by luck) stays enrolled; re-enrollment is the remedy. BCHAT-007 (key bound to user authentication) is not addressed.

## Review

- 2026-10-23 17:33 · approve · PR #347 merged into main @ 3bb5346 (recorded by kanban-sync)

## Activity log

- 2026-10-23 19:30 · claude · (new) → backlog · created from GitHub issue #285 (BCHAT-032); blockers scan: none
- 2026-10-23 17:14 · scholtz · taken, and session-20261023-1714 · backlog → in-progress · claimed
- 2026-10-23 17:33 · user · in-progress → done · PR #347 merged into main @ 3bb5346 (synced by kanban-sync)
