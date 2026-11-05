---
id: T-232
title: "Authenticator: minimum platform version and update enforcement"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-230]
area: ["apps/biatec_authenticator/android/app/build.gradle.kts", "apps/biatec_authenticator/ios/Runner.xcodeproj/**", "apps/biatec_authenticator/lib/update/**", "apps/biatec_authenticator/test/update/**"]
parallel: no
conflicts_with: [T-234]
issue: "#384"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-05
updated: 2026-11-05
---

# T-232 - Authenticator: minimum platform version and update enforcement

## Context

From the MASVS matrix (T-230): MASVS-CODE-1 (the app requires an up-to-date platform version) and MASVS-CODE-2 (a mechanism for enforcing app updates) are open. Today `minSdk` is the Flutter default and nothing tells an old build to update. Read `docs/AUTHENTICATOR-SECURITY.md` (rows CODE-1 and CODE-2) and `PRODUCT-DESCRIPTION.md` (Security Standard For The App).

## Goal

The app refuses to run on an operating system older than the declared minimum, and a build that Biatec OIDC marks as too old tells the user to update and does not open the secure area.

## Scope

- In: set and document `minSdk` and the iOS deployment target from a security reason; a minimum-version check (a `min_app_version` the app reads from a Biatec OIDC response header or a signed config, fail closed when the build is older, with the mock supplying it); an "update required" screen that links to the store and names nothing else; tests for each state.
- Out: the store listing, and changes to Biatec OIDC (record the needed header in `docs/required-changes-biatec-oidc.md` with the fallback: a compiled-in minimum).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-234: they both change `android/app/build.gradle.kts`.

Ordering: T-230 writes `docs/AUTHENTICATOR-SECURITY.md`, whose rows this task updates, so it goes first.

## Acceptance criteria

- [ ] A build older than the minimum shows the update screen and the secure area stays closed (widget test with an injected version).
- [ ] The minimum platform versions are set in the build files and named in the matrix row with the reason.
- [ ] `docs/AUTHENTICATOR-SECURITY.md` rows MASVS-CODE-1 and MASVS-CODE-2 move from open to met or partly, with links.
- [ ] `flutter analyze` is clean and `flutter test` passes.

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; say in the hand-off what could not be run on a device.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-11-05 10:20 · claude · (new) → backlog · created from the MASVS matrix of T-230 (open item)
- 2026-11-05 10:25 · claude · ordering and file overlap decided: blocked_by T-230 (matrix rows), conflicts_with T-234 (build.gradle.kts)
- 2026-11-05 10:04 · kanban-board · backlog → blocked · waiting on: T-230
- 2026-11-05 11:27 · kanban-board · blocked → backlog · blockers done: T-230
