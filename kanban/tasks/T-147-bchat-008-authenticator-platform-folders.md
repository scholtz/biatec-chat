---
id: T-147
title: "Commit the authenticator's android/ and ios/ folders with hardening applied and the keystore plugins wired (BCHAT-008)"
status: backlog
priority: P2
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/android/**", "apps/biatec_authenticator/ios/**", "apps/biatec_authenticator/native/**", "apps/biatec_authenticator/test/platform_hardening_test.dart", "apps/biatec_authenticator/README.md", ".gitignore"]
parallel: yes
conflicts_with: []
issue: "#261"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-147 - Commit the authenticator's android/ and ios/ folders with hardening applied and the keystore plugins wired (BCHAT-008)

## Context

Security audit finding BCHAT-008 (Medium, issue #261, RISK-003). The authenticator's `android/` and `ios/` folders are not in the
repository (the README says to generate them with `flutter create`), and the Keystore and Secure Enclave plugins under `native/` are not
wired in. Every native release property (FLAG_SECURE, `allowBackup`, cleartext policy, ATS, exported components, entitlements, debuggable,
signing) is therefore undefined and unreviewable. The release-build part of the finding (no mock default, fail without `OIDC_BASE_URL`)
was done in T-210.

## Goal

The generated platform folders are committed with the hardening applied, the two plugins are wired and live where the build compiles them, and a
test keeps the native posture from regressing.

## Scope

- In: `flutter create --platforms=android,ios` output committed; Android: no backup, no cleartext in release (cleartext only in the debug overlay for
  the local mock), one exported component (the launcher activity), FLAG_SECURE on the window, `KeystorePlugin` wired in `MainActivity`, release signing
  taken from an untracked `key.properties` and refused when missing (never the debug key); iOS: default ATS, a Face ID usage string, no URL schemes,
  `KeystorePlugin.swift` in the Runner target and registered in `AppDelegate`; `native/` content moved to its real place; `.gitignore` for build output
  and signing material; a Dart test over these files; README.
- Out: the Keystore key parameters themselves (user authentication, security-level checks: BCHAT-007 and BCHAT-032, own tasks); an iOS build (no Xcode
  here); signing material, store listings, CI mobile builds.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `git ls-files apps/biatec_authenticator` lists `android/` and `ios/` sources and no build output or signing material.
- [ ] The Android manifest has `allowBackup="false"`, no cleartext in the main manifest, exactly one exported component, and the activity sets FLAG_SECURE; the release build type does not use the debug signing key.
- [ ] `KeystorePlugin` is registered on Android and iOS, and `native/` no longer holds a second copy.
- [ ] `test/platform_hardening_test.dart` checks these properties and `flutter test` passes; the Android project builds if the SDK licenses allow it here.

## Verification

`flutter test` in `apps/biatec_authenticator`; `flutter build apk --debug` where possible.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 18:30 · claude · (new) → backlog · created from GitHub issue #261 (BCHAT-008); blockers scan: none
