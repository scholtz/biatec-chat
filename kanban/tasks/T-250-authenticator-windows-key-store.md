---
id: T-250
title: "Authenticator: a protected key store for Windows (TPM and Windows Hello), so a desktop build can enroll"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-248]
area: ["apps/biatec_authenticator/windows/runner/**", "apps/biatec_authenticator/lib/keys/**", "apps/biatec_authenticator/lib/lock/**", "apps/biatec_authenticator/test/keys/**"]
parallel: yes
conflicts_with: []
issue: "#423"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-16
---

# T-250 · Authenticator: a protected key store for Windows

## Context

Found while doing T-248. A Windows build can sign in with the system browser, but the enrollment stops at key
creation with "This build of the app has no protected key store on this kind of device"
(`EnrollmentController.noKeyStoreError`). The device keys are made by native code behind the channel
`io.biatec.authenticator/keystore`, which exists for Android (`KeystorePlugin.kt`) and iOS (`KeystorePlugin.swift`)
only. The app fails closed on a key that is not in hardware (`KeySecurityLevel.isHardware`), and that rule stays.

PRODUCT-DESCRIPTION, "Authenticator App (Flutter)": two device keys in hardware, the approval key usable only after
the user's biometric for each signature.

## Goal

A Windows build enrolls a device whose keys are held by the computer's TPM, and an approval needs Windows Hello.

## Scope

- In: a Windows implementation of the keystore channel in `windows/runner` (C++): P-256 keys in the Microsoft
  Platform Crypto Provider (TPM 2.0) through CNG/NCrypt, not exportable; the approval key gated by Windows Hello for
  each signature; the security level reported honestly (`software` when there is no TPM, which the app then refuses);
  the biometric lock of the secure area on Windows; unit tests of the Dart side; a manual checklist.
- Out: Linux and macOS key stores; platform attestation for Windows.

## Parallel work

Parallel work possible: **yes**. It is blocked by T-248, and T-249 changes other files.

## Acceptance criteria

- [ ] On a Windows computer with a TPM the enrollment completes after the browser sign-in, and the key protection
      card says the keys are in hardware.
- [ ] A computer without a TPM is refused with the existing "held in software" message; no key is kept.
- [ ] An approval asks for Windows Hello, and a cancelled prompt signs nothing.
- [ ] The private keys cannot be exported (checked against the provider's key properties).
- [ ] `flutter build windows` builds; `flutter test` passes.

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

Then the manual checklist on a Windows computer with Windows Hello set up.

## Hand-off

## Review

## Activity log

- 2026-11-16 12:00 · session-20261116-0900 · (new) → blocked · created as a follow-up of T-248, which it waits for
- 2026-11-16 11:00 · kanban-board · backlog → blocked · waiting on: T-248
