---
id: T-253
title: "Authenticator: CI passes again on Flutter 3.47+ (material_ui and cupertino_ui instead of the deprecated imports)"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/**", "apps/biatec_authenticator/test/**", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/pubspec.lock"]
parallel: no
conflicts_with: [T-249, T-251]
issue: "#428"
branch: task/T-253
owner: session-20261117-1730
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-17
updated: 2026-11-17
---

# T-253 · Authenticator: CI passes again on Flutter 3.47+

## Context

Since Flutter 3.47, `package:flutter/material.dart` and `package:flutter/cupertino.dart` are deprecated in favour of the
packages `material_ui` and `cupertino_ui`. On the stable Flutter of CI (3.50), `flutter analyze` reports about 100
`deprecated_member_use` infos, one per import, and exits with an error. The `authenticator` CI job has therefore failed
on every pull request that touches the app since T-248; T-248, T-250 and T-252 were merged with it red.

## Goal

The `authenticator` CI job passes again, so that a real analyzer finding is seen.

## Scope

- In: add `material_ui` and `cupertino_ui` to `pubspec.yaml`; replace the two imports in `lib/` and `test/`.
- Out: any other change of the app (T-251 translates the texts and comes after this task).

## Parallel work

Parallel work possible: **no**. It touches the imports of nearly every file under `lib/` and `test/`, as T-249 and
T-251 do.

## Acceptance criteria

- [ ] `flutter analyze` reports no issues on Flutter 3.50.
- [ ] `flutter test` passes, and `flutter build windows --release` builds.
- [ ] The `authenticator` CI job passes on the pull request.

## Verification

`flutter analyze`, `flutter test`, `flutter build windows --release` in `apps/biatec_authenticator`; the CI run of the
pull request.

## Hand-off

## Review

## Activity log

- 2026-11-17 17:30 · scholtz · (new) → backlog · created while merging T-250 and T-252 (both merged with the authenticator job red); blockers scanned: shares files with T-249 and T-251 (conflicts_with), no ordering need
- 2026-11-17 17:30 · scholtz · taken · status stays backlog
- 2026-11-17 17:30 · scholtz · backlog → in-progress · claimed; branch task/T-253, before T-251
