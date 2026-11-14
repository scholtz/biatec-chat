---
id: T-242
title: "Authenticator: enroll through an in-app Biatec OIDC login, activate a new device from an existing one with a pairing code (BCHAT-033)"
status: done
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-241]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/devices/**", "apps/biatec_authenticator/lib/config.dart", "apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/ios/Runner/Info.plist", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/test/devices/**", "packages/consent-protocol/mock-oidc/**"]
parallel: yes
conflicts_with: []
issue: "#286"
branch: "task/T-242"
owner: "session-20261115-0000"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-12
updated: 2026-11-14
---

# T-242 - Authenticator: in-app login for enrollment, and activation by an existing device (BCHAT-033)

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decision D10. Server side: ludovit-scholtz/AlgorandGoogleDriveAccount#31. Audit finding BCHAT-033 (issue 286).

Enrollment asks the user to paste a login token (`lib/enrollment/enrollment_screen.dart` lines 267 to 317). A pasted token says nothing about how fresh the login is or who obtained it, and it trains users to paste bearer tokens into an app. Separately, the mock makes a new device active at once when the request names an active device's `kid` (`approved_by_kid`, `mock-oidc/src/app.ts` line 518): knowing a `kid` proves nothing.

## Goal

A device is enrolled only by a person who just logged in inside the app, and becomes active at once only when an existing device's user confirmed the same pairing code on both phones (or a recovery code was entered).

## Scope

- In:
  - **Login.** Authorization code with PKCE through the system browser (`flutter_appauth`: Custom Tabs on Android, `ASWebAuthenticationSession` on iOS; never an embedded web view). Client `OIDC_CLIENT_ID` (dart-define), redirect by app link / universal link with a custom-scheme fallback only for debug builds, scope `openid enroll-device`, `prompt=login`, `max_age=0`. The access token is held in memory for the one `POST /devices/challenge` and `POST /devices` and then dropped; nothing from the login is stored. No refresh token is requested.
  - The paste field stays only when the app targets the mock in a debug build; it is not compiled into release builds (assert with a test on the release configuration flag).
  - **Server refusals.** `401` with `insufficient_user_authentication` and `max_age` → "Sign in again" and restart the login once.
  - **Activation by an existing device.** The new device enrolls (inactive) and shows the 6 digit pairing code computed as T-239 defines. Each active device of the account gets the `device_enrolled` event over the device channel (T-244; no Firebase) (and sees pending devices in `GET /devices`): the Devices screen shows "Activate <name>?" with the pairing code the server data yields. The user compares the two phones; on confirm the existing device signs `biatec_device_approval` with its approval key (one biometric prompt) and calls `POST /devices/{new_kid}/activate`. The new device polls `GET /devices` (5 s, while the screen is open) and moves to active. Mismatch or doubt → "Cancel this device" (`POST /devices/{kid}/cancel`).
  - **Activation by recovery code.** A field on the enrollment screen; the code goes in `POST /devices` as `activation: { kind: "recovery_code", code }`; wrong code and lockout (`429`) have their own texts.
  - **No activation** → the existing 24 hour waiting state, with the text that an email with a cancel link was sent to the account's address.
  - **Mock.** `enroll-device` scope and `auth_time` check on `POST /devices` (seed login tokens gain both; `login-alice-stale` for the refusal), `POST /devices/{kid}/activate` verifying the signed approval with the approval key and the pairing code, recovery-code activation, `approved_by_kid` removed (`400`), a `device_enrolled` entry in the push outbox, and a minimal authorize/token pair for the PKCE flow so the app's flow can be tested against it.
  - English and Slovak strings.
- Out: sending the email (server, issue 31); platform attestation (T-234 is done; server verification is AlgorandGoogleDriveAccount issue 36).

## Parallel work

Parallel work possible: **yes** (after T-241).

## Acceptance criteria

- [x] Dart tests with a fake auth client: the login requests exactly the scope, `prompt` and `max_age` above; the token is not written to any store (the fake secure storage and preferences stay empty of it); a stale-login refusal restarts once.
- [x] Pairing code: the app's function reproduces the T-239 worked example.
- [x] Activation flow against the mock: confirm → active; a signature by the presence key, a wrong pairing code and an approver of another account are refused; `approved_by_kid` is refused.
- [x] Recovery-code activation, wrong code and lockout are covered.
- [x] A release-mode build configuration has no paste field (test on the flag that gates it).
- [ ] `flutter analyze` and `flutter test` pass; `pnpm --filter mock-oidc test` passes.
- [ ] Manual checklist in the README for a person with two phones; the Hand-off says whether it was run.

## Implementation notes

- App links need the server to host `/.well-known/assetlinks.json` and `apple-app-site-association`; record that as a deployment prerequisite in `docs/required-changes-biatec-oidc.md` and do not block on it (debug builds use the custom scheme).
- The login must not reuse a browser session silently: that is what `prompt=login` and `max_age=0` are for, and the server enforces `auth_time` regardless.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test && cd ../.. && pnpm --filter mock-oidc test`

## Hand-off

PR #415, `task/T-242` @ 1175270. Local: `flutter test` 1296 pass, mock-oidc 67, the authenticator contract tests against the mock pass (the proof-mode contract test needs a mock with `MOCK_DEVICE_PROOF=1`). CI: backend, js, images, authenticator-security pass; `authenticator` fails only on the Flutter SDK drift that also fails on main.

- In-app login (PKCE in the system browser, scope `openid enroll-device`, `prompt=login`, `max_age=0`), token in memory only; paste field only for the mock in debug builds; activation by an existing device with the pairing code and a `biatec_device_approval` signed with the approval key; recovery-code activation (format checked locally); the waiting state polls `GET /devices` and handles a device cancelled elsewhere.
- Gated by configuration against real Biatec OIDC: `IN_APP_LOGIN` (on for the mock), activation additionally needs `TWO_KEY_DEVICE`. A release build against a real URL with the login off has no paste field and cannot enroll: it needs `IN_APP_LOGIN=true` and the hosted app links.
- Mock: `enroll-device` scope and `auth_time`, `/authorize` + `/token`, `/devices/{kid}/activate` (verified with `verifyDeviceSigned`), recovery-code lifecycle (issued, acknowledged, consumed), `approved_by_kid` refused.
- NOT verified on a device: the system-browser flow and app-link return, iOS associated domains and session, the biometric prompt, the two-phone checklist. No device criterion is ticked. Deployment prerequisite: `assetlinks.json` and `apple-app-site-association` on Biatec OIDC (recorded in `docs/required-changes-biatec-oidc.md`).
- Known limits: the release https redirect on iOS needs iOS 17.4 (the app's target is 15.0): the app refuses to start an https login on older iOS with a clear text, raising the target is the alternative; a wrong recovery code needs a new login (the token is dropped and `max_age=0`); wrong codes count toward the lockout; error classification in the enrollment banner matches the controller's error strings.

**Unblocks:** T-245 (user-held trust settings).

## Review

- 2026-11-14 23:55 · approve · PR #415 merged into main @ 8a1bcf5 (recorded by kanban-sync)

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-241
- 2026-11-13 09:05 · scholtz · blocked → blocked · wording: device channel instead of push (revision 2)
- 2026-11-14 23:02 · kanban-board · blocked → backlog · blockers done: T-241
- 2026-11-15 00:00 · scholtz · backlog → in-progress · claimed by session-20261115-0000 on task/T-242
- 2026-11-15 02:10 · scholtz · in-progress → review · PR #415 @ 1175270
- 2026-11-14 23:55 · user · review → done · PR #415 merged into main @ 8a1bcf5 (synced by kanban-sync)
