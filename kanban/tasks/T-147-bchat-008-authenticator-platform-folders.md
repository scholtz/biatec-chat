---
id: T-147
title: "Commit the authenticator's android/ and ios/ folders with hardening applied and the keystore plugins wired (BCHAT-008)"
status: in-progress
priority: P2
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/android/**", "apps/biatec_authenticator/ios/**", "apps/biatec_authenticator/native/**", "apps/biatec_authenticator/test/platform_hardening_test.dart", "apps/biatec_authenticator/README.md", ".gitignore"]
parallel: yes
conflicts_with: []
issue: "#261"
branch: "task/T-147"
owner: "session-20261023-1633"
taken_by: "scholtz"
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

- [x] `git ls-files apps/biatec_authenticator` lists `android/` and `ios/` sources and no build output or signing material.
- [x] The Android manifest has `allowBackup="false"`, no cleartext in the main manifest, exactly one exported component, and the activity sets FLAG_SECURE; the release build type does not use the debug signing key.
- [x] `KeystorePlugin` is registered on Android and iOS, and `native/` no longer holds a second copy.
- [x] `test/platform_hardening_test.dart` checks these properties and `flutter test` passes; the Android project builds if the SDK licenses allow it here.

## Verification

`flutter test` in `apps/biatec_authenticator`; `flutter build apk --debug` where possible.

## Hand-off

- **Summary:** apps/biatec_authenticator/android and ios are committed (flutter create, then hardened). Android: application id io.biatec.authenticator, no backup/transfer (allowBackup=false, data_extraction_rules.xml), cleartext forbidden in the main manifest and allowed only by the debug manifest, MainActivity (FlutterFragmentActivity for local_auth) is the only component of the app's own manifest, FLAG_SECURE, KeystorePlugin registered, release builds minified and signed from a git-ignored key.properties (the build fails without it; never the debug key). iOS: default ATS, no URL schemes, Face ID usage string, KeystorePlugin registered in AppDelegate, a new ScreenCover hides content in the app switcher and while recording, both Swift files added to the Runner target, bundle id io.biatec.authenticator. native/ is gone: the plugins live where the build compiles them. test/platform_hardening_test.dart keeps these properties; README documents the posture and the release build.
- **Files changed:** apps/biatec_authenticator/android/**, apps/biatec_authenticator/ios/**, apps/biatec_authenticator/test/platform_hardening_test.dart, apps/biatec_authenticator/README.md, apps/biatec_authenticator/.metadata, native/ removed.
- **How it was verified:** flutter build apk --debug and --release (with a throwaway keystore, deleted afterwards) succeed, so the Kotlin plugin compiles and R8 accepts the app; the release build without key.properties fails with 'SigningConfig release is missing required property storeFile'; the merged release manifest has allowBackup false, cleartext false, not debuggable, application id io.biatec.authenticator, and exported components only the launcher plus three library receivers guarded by system permissions; flutter test (140 passed incl. the new test) and flutter analyze are clean.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** Nothing was run on a device or emulator, and nothing iOS was compiled (no Xcode): KeystorePlugin.swift, ScreenCover.swift, the AppDelegate change and the hand-edited project.pbxproj entries need a first build on a Mac. The default Flutter launcher icons are still in place. Firebase messaging (already a dependency) brings its google-services config requirement: no google-services.json / GoogleService-Info.plist is committed, so push is not functional until the owner adds them. Whether the iOS app should also ask for ITSAppUsesNonExemptEncryption is the owner's export-compliance call. BCHAT-007 (user-authentication-bound key) and BCHAT-032 (security-level check) are still open and now have compiled code to build on.

## Review

## Activity log

- 2026-10-23 18:30 · claude · (new) → backlog · created from GitHub issue #261 (BCHAT-008); blockers scan: none
- 2026-10-23 16:33 · scholtz · taken, and session-20261023-1633 · backlog → in-progress · claimed
