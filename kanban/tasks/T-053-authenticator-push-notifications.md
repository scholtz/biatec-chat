---
id: T-053
title: Authenticator push notifications
status: done
priority: P3
epic: "Consent & authenticator"
blocked_by: [T-052]
area: ["apps/biatec_authenticator/lib/push/**"]
parallel: yes
conflicts_with: []
issue: "#54"
branch: "task/T-053"
owner: "session-20261019-2300"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-19
updated: 2026-10-19
---

# T-053 · Authenticator push notifications

## Context

FCM/APNs carrying only a request id; content always fetched from OIDC.

## Goal

Approvals arrive without manual refresh.

## Scope

- In: Push plugin setup, token registration with mock, handler opening the pending request.
- Out: Production push credentials.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] Push payload contains only request id (mock outbox test; the app reads only `auth_req_id`)
- [x] Tapping opens the right request (widget and controller tests, contract test against the mock)
- [ ] Works on Android emulator with test FCM: not run, no emulator or Firebase project here and the native folders are not generated
- [x] No secrets committed (Firebase options only through dart-defines)

## Verification

manual emulator test

## Hand-off

- **Summary:** `lib/push/`: `PushController` (registration at Biatec OIDC via `PUT /devices/push`, tap opens the request by id, foreground push refreshes the pending list and shows an in-app notice), `FcmPushTransport` (`firebase_messaging` 17, FID token, off unless the Firebase dart-defines are set), id-only payload parsing. Polling stays at 5 s as the fallback. Mock OIDC accepts push tokens and records id-only payloads in `GET /mock/push-outbox`.
- **Branch:** `task/T-053` @ 4b6852c, PR #111 (three review rounds; remaining edge cases listed in the PR).
- **Files outside the area:** `lib/main.dart` (wiring), `lib/approvals/pending_screen.dart` (push refresh, rerun), `lib/approvals/approval_screen.dart` (named route), `lib/approvals/approval_client.dart` (`registerPush`), `packages/consent-protocol/mock-oidc/**`, `docs/required-changes-biatec-oidc.md` §4, app README.
- **How it was verified:** `flutter analyze` clean; `flutter test test/push` 15 passed; full `flutter test` with `MOCK_OIDC_URL` against the running mock: only the 2 known T-051 enrollment failures (also on main); `pnpm --filter mock-oidc test` 21 passed, typecheck clean.
- **Follow-up tasks created:** none.
- **Unblocks:** no task is blocked by T-053
- **Open questions:** a Firebase project and generated Android/iOS folders are needed for the emulator check.

## Review

- 2026-10-19 22:40 · approve · PR #111 merged into main @ ade4b18 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:37 · kanban-board · blocked → backlog · blockers done: T-052
- 2026-10-19 22:16 · LukassSoukup · taken · status stays backlog
- 2026-10-19 22:16 · session-20261019-2300 · backlog → in-progress · claimed via /kanban-take + /kanban-next loop
- 2026-10-19 22:40 · session-20261019-2300 · in-progress → review · PR #111, three review rounds
- 2026-10-19 22:40 · user · review → done · PR #111 merged into main @ ade4b18 (synced by kanban-sync)
