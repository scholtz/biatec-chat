---
id: T-242
title: "Authenticator: enroll through an in-app Biatec OIDC login, activate a new device from an existing one with a pairing code (BCHAT-033)"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-241]
area: ["apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/lib/devices/**", "apps/biatec_authenticator/lib/config.dart", "apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/ios/Runner/Info.plist", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/test/devices/**", "packages/consent-protocol/mock-oidc/**"]
parallel: yes
conflicts_with: []
issue: "#286"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-12
updated: 2026-11-13
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

- [ ] Dart tests with a fake auth client: the login requests exactly the scope, `prompt` and `max_age` above; the token is not written to any store (the fake secure storage and preferences stay empty of it); a stale-login refusal restarts once.
- [ ] Pairing code: the app's function reproduces the T-239 worked example.
- [ ] Activation flow against the mock: confirm → active; a signature by the presence key, a wrong pairing code and an approver of another account are refused; `approved_by_kid` is refused.
- [ ] Recovery-code activation, wrong code and lockout are covered.
- [ ] A release-mode build configuration has no paste field (test on the flag that gates it).
- [ ] `flutter analyze` and `flutter test` pass; `pnpm --filter mock-oidc test` passes.
- [ ] Manual checklist in the README for a person with two phones; the Hand-off says whether it was run.

## Implementation notes

- App links need the server to host `/.well-known/assetlinks.json` and `apple-app-site-association`; record that as a deployment prerequisite in `docs/required-changes-biatec-oidc.md` and do not block on it (debug builds use the custom scheme).
- The login must not reuse a browser session silently: that is what `prompt=login` and `max_age=0` are for, and the server enforces `auth_time` regardless.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test && cd ../.. && pnpm --filter mock-oidc test`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-241
- 2026-11-13 09:05 · scholtz · blocked → blocked · wording: device channel instead of push (revision 2)
