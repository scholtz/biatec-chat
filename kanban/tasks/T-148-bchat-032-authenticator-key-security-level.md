---
id: T-148
title: "Report the real security level of the authenticator's device key and refuse a software key (BCHAT-032)"
status: in-progress
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

- [ ] Both plugins return `securityLevel` with `generate` and `load`; Android refuses to keep a key whose level is software (the key is deleted and `insecure_key` is returned).
- [ ] `PlatformDeviceKeyStore` parses the level (a missing or unknown value is `unknown`) and `EnrollmentController.enroll` refuses `software` and `unknown`, deleting the key.
- [ ] The enrollment screen no longer claims secure hardware unconditionally: it shows the level the key got.
- [ ] `flutter test`, `flutter analyze` and `flutter build apk --debug` pass.

## Verification

`flutter test`, `flutter analyze`, `flutter build apk --debug` in `apps/biatec_authenticator`.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 19:30 · claude · (new) → backlog · created from GitHub issue #285 (BCHAT-032); blockers scan: none
- 2026-10-23 17:14 · scholtz · taken, and session-20261023-1714 · backlog → in-progress · claimed
