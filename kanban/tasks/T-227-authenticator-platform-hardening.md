---
id: T-227
title: "Authenticator: platform hardening (no backups, no screenshots, release build)"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/android/app/build.gradle*", "apps/biatec_authenticator/android/app/proguard-rules.pro", "apps/biatec_authenticator/android/app/src/main/**/MainActivity*", "apps/biatec_authenticator/ios/Runner/AppDelegate.swift", "apps/biatec_authenticator/ios/Runner/Info.plist", "apps/biatec_authenticator/ios/Runner/PrivacyInfo.xcprivacy", "apps/biatec_authenticator/test/platform_hardening_test.dart", "apps/biatec_authenticator/README.md"]
parallel: no
conflicts_with: [T-229]
issue: "#364"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-227 - Authenticator: platform hardening (no backups, no screenshots, release build)

## Context

Spec: "Security Standard For The App" rules 4 and 5 and Secure Area rule 5. `test/platform_hardening_test.dart` already checks some of the manifest; extend it.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

The platform folders and build settings enforce: no backups, no screenshots of the secure area, no debuggable or cleartext builds, and an obfuscated release.

## Scope

- In: Android `allowBackup=false`, `dataExtractionRules` and `fullBackupContent` excluding everything, `FLAG_SECURE` on the activity (the secure area; the window flag is set when locked and unlocked alike so nothing of the app appears in the recents), `android:debuggable` false in release, `usesCleartextTraffic=false` (the network security config with pins is T-229), `allowClearTextTraffic` nowhere, R8 minify and resource shrink on, exported components minimal, `FLAG_SECURE`-independent blank recents; iOS: files excluded from backup, a cover view shown when the app resigns active and when a screen capture is detected (`UIScreen.isCaptured`), keychain items `ThisDeviceOnly` (the existing check stays), ATS with no exceptions, `PrivacyInfo.xcprivacy`; release builds with `--obfuscate --split-debug-info` documented and used by CI; the tests read the manifests and Info.plist and fail on regression.
- Out: TLS pinning and the network security config (T-229), integrity checks (T-228).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-229: they change the same files.

## Acceptance criteria

- [ ] A test reads AndroidManifest.xml and fails if `allowBackup` is not false, `debuggable` is set, cleartext is allowed, or an exported component is added without being listed.
- [ ] `FLAG_SECURE` is set before the first frame (a test of the activity source) and the iOS cover view exists for resign-active and capture (source test).
- [ ] The Android data extraction rules exclude every domain; the iOS vault and database paths have the exclude-from-backup attribute set (source test).
- [ ] The CI build command for release uses `--obfuscate` and `--split-debug-info` and publishes the symbols as a private artifact.
- [ ] README lists the settings and how to verify them by hand with `adb` and a screenshot attempt.
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

- 2026-10-28 09:00 · claude · (new) → backlog · created from the request to add two-factor codes, a biometric lock and industry-standard security
