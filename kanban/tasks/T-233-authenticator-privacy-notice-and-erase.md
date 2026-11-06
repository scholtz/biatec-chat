---
id: T-233
title: "Authenticator: privacy notice and erase-everything"
status: done
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-230]
area: ["apps/biatec_authenticator/lib/privacy/**", "apps/biatec_authenticator/lib/settings/settings_screen.dart", "apps/biatec_authenticator/test/privacy/**", "docs/AUTHENTICATOR-SECURITY.md"]
parallel: yes
conflicts_with: []
issue: "#385"
branch: "task/T-233"
owner: "session-20261105-1700"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-05
updated: 2026-11-06
---

# T-233 - Authenticator: privacy notice and erase-everything

## Context

From the MASVS matrix (T-230): MASVS-PRIVACY-3 (transparency about data collection) and MASVS-PRIVACY-4 (user control over data) are partly met. The app collects only a push token and a device key id, but the user is told nowhere in the app, and there is no single action that removes everything from the phone. Read `docs/AUTHENTICATOR-SECURITY.md` (rows PRIVACY-3 and PRIVACY-4) and `PRODUCT-DESCRIPTION.md` (Privacy).

## Goal

The user can read, inside the app, exactly what it stores and sends, and can erase everything it holds on the phone after a fresh biometric.

## Scope

- In: a Privacy screen under Settings > About (what is stored on the phone, what is sent to Biatec OIDC and to the push service, what is never collected); an "Erase everything on this phone" action (fresh biometric, a confirmation that says what is lost, then: delete the vault and its keys, the enrollment and device key, history, preferences, the version counter; sign out) with tests that each store is empty afterwards; the store privacy labels text kept in `docs/`.
- Out: erasing server-side data (a Biatec OIDC matter; say so on the screen).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

Ordering: T-230 writes `docs/AUTHENTICATOR-SECURITY.md`, whose rows this task updates, so it goes first.

## Acceptance criteria

- [ ] The Privacy screen lists every item the app stores or sends, and a test fails when a new storage key is added without being listed.
- [ ] Erase needs a fresh biometric and a confirmation; afterwards the vault file, vault keys, enrollment, device key, history, theme, lock delay and codes preferences are gone (tests with fakes).
- [ ] `docs/AUTHENTICATOR-SECURITY.md` rows MASVS-PRIVACY-3 and MASVS-PRIVACY-4 are updated with links.
- [ ] `flutter analyze` is clean and `flutter test` passes; screens hold at 3x text in light and dark.

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; say in the hand-off what could not be run on a device.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

- 2026-11-06 13:03 · approve · PR #389 merged into main @ b5fcecb (recorded by kanban-sync)

## Activity log

- 2026-11-05 10:20 · claude · (new) → backlog · created from the MASVS matrix of T-230 (open item)
- 2026-11-05 10:25 · claude · ordering decided: blocked_by T-230 (matrix rows)
- 2026-11-05 10:04 · kanban-board · backlog → blocked · waiting on: T-230
- 2026-11-05 11:27 · kanban-board · blocked → backlog · blockers done: T-230
- 2026-11-05 17:00 · scholtz · backlog → in-progress · claimed by session-20261105-1700 on task/T-233
- 2026-11-06 13:03 · user · in-progress → done · PR #389 merged into main @ b5fcecb (synced by kanban-sync)
