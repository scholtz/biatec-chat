---
id: T-212
title: "Authenticator: iOS keychain items stay on this device (BCHAT-009)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/lib/security/secure_storage.dart", "apps/biatec_authenticator/lib/enrollment/enrollment_store.dart", "apps/biatec_authenticator/lib/approvals/history_store.dart", "apps/biatec_authenticator/lib/main.dart", "apps/biatec_authenticator/test/**"]
parallel: yes
conflicts_with: []
issue: "#262"
branch: "task/T-212"
owner: "session-20261023-1413"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-212 - Authenticator: iOS keychain items stay on this device (BCHAT-009)

## Context

Security audit finding BCHAT-009 (issue #262, Medium-High, RISK-003). The enrollment record (device token, attestation, recovery state), the alias and the approval history are stored with `const FlutterSecureStorage()`, whose iOS default accessibility is `unlocked`: those keychain items are included in encrypted backups and can be restored on another device, together with the device token, which is a long-lived bearer. Android is not affected (the keystore key is hardware bound and the secure storage file is excluded by the platform defaults the project will set).

## Goal

Every secure-storage item of the authenticator is kept with `unlocked_this_device` accessibility (never in a backup, never restored elsewhere), and items written before the change are rewritten once so they get the attribute too.

## Scope

- In: one shared storage factory with the iOS option, its use by both stores, a one-time rewrite of existing items at start, tests.
- Out: the Android manifest (`allowBackup=false`) and iOS `NSFileProtection` settings, which belong to the platform projects (BCHAT-008).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Both stores default to a storage created with `KeychainAccessibility.unlocked_this_device`.
- [x] At start, existing enrollment, alias and history items are deleted and written again once (marker `biatec.storage.v2`), with their values unchanged; running it again does nothing.
- [x] `flutter analyze` and `flutter test` pass.

## Verification

`flutter analyze` and `flutter test` in apps/biatec_authenticator.

## Hand-off

- **Summary:** `lib/security/secure_storage.dart`: `deviceBoundStorage` (`KeychainAccessibility.unlocked_this_device`) is the default of the enrollment and history stores, which now share the key constants the migration walks. `rewriteWithDeviceBoundAccessibility()` (called from `main()`, bounded to 3 s) rewrites the existing items once through a staging copy (copy, delete, write back, drop the copy), per key; a leftover staging item is restored at the next run; the marker `biatec.storage.v2` is written only when every key went through; an unavailable keychain never stops the start.
- **Verification:** `flutter analyze` no issues; `flutter test` all pass (6 in security_storage_test.dart, including a write that fails after the delete and an unavailable keychain). Not verified on a real iOS device or backup (no iOS project in the repository, BCHAT-008).
- **Out of scope here:** Android `allowBackup`, iOS file protection (platform projects).

## Review

- 2026-10-23 14:48 · approve · PR #339 merged into main @ 0e6ae28 (recorded by kanban-sync)

## Activity log

- 2026-10-23 14:13 · kanban-split · (new) → backlog · created from audit issue #262
- 2026-10-23 14:13 · scholtz · taken · status stays backlog
- 2026-10-23 14:13 · session-20261023-1413 · backlog → in-progress · claimed, branch task/T-212
- 2026-10-23 14:48 · user · in-progress → done · PR #339 merged into main @ 0e6ae28 (synced by kanban-sync)
