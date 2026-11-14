---
id: T-244
title: "Authenticator: device channel over SignalR (no Firebase), Android stay-connected service, optional Apple wake"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-240]
area: ["apps/biatec_authenticator/lib/push/**", "apps/biatec_authenticator/lib/channel/**", "apps/biatec_authenticator/lib/settings/**", "apps/biatec_authenticator/pubspec.yaml", "apps/biatec_authenticator/android/**", "apps/biatec_authenticator/ios/Runner/**", "apps/biatec_authenticator/test/push/**", "apps/biatec_authenticator/test/channel/**", "apps/biatec_authenticator/README.md", "packages/consent-protocol/mock-oidc/**"]
parallel: yes
conflicts_with: []
issue: "#404"
branch: "task/T-244"
owner: "session-20261114-1630"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-13
updated: 2026-11-14
---

# T-244 - Authenticator: device channel without Firebase

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decision D12 (revision 2). Server side: ludovit-scholtz/AlgorandGoogleDriveAccount#29; read it first, it defines the hub, the ticket and the messages.

Owner decision (2026-11-13): the product must not depend on Firebase. The app uses `firebase_core` and `firebase_messaging` today (`pubspec.yaml` lines 19 and 20, `lib/push/fcm_push_transport.dart`). The replacement is a SignalR hub hosted inside Biatec OIDC. Its limit is stated in the record: nothing we host can wake an app that is not running. Android gets an opt-in foreground service; iOS can be woken only by Apple's push service, which is optional and off by default.

## Goal

The app has no Firebase or Google push code, learns about a new request at once while it is connected, and the user can choose to stay connected on Android.

## Scope

- In:
  - **Remove Firebase.** Delete `firebase_core`, `firebase_messaging`, `fcm_push_transport.dart`, the Google services Gradle plugin and any `google-services.json` / `GoogleService-Info.plist` reference, and the related manifest and `Info.plist` entries.
  - **Channel client** (`lib/channel/device_channel.dart`, behind the existing transport interface in `lib/push/push_transport.dart`, renamed `DeviceEventSource`). A minimal SignalR JSON hub-protocol client over `web_socket_channel`, server-to-client only, written here (about 150 lines; no SignalR package, to keep the dependency surface of a security app small):
    1. `POST /devices/hub-ticket` with device auth and the device proof → `{ ticket, expires_in }`.
    2. Connect `wss://<oidc>/hubs/device?access_token=<ticket>` (WebSockets with negotiation skipped). The ticket is single use: fetch a new one for every connection. Never put the device token in a URL.
    3. Send the handshake `{"protocol":"json","version":1}` followed by the record separator `0x1e`; expect `{}` + `0x1e`. Every later frame is JSON + `0x1e`; a frame may carry several messages.
    4. Handle message type 1 (invocation: `target`, `arguments[0]`), type 6 (ping: answer with a ping, and send one every 15 seconds), type 7 (close: reconnect unless `allowReconnect` is false). Ignore unknown types and unknown targets. Never send an invocation.
    5. Reconnect with backoff 1, 2, 5, 10, 30 seconds plus up to 20 percent jitter, reset after a connection that lived 60 seconds. The server closes a connection after 30 minutes; that is a normal reconnect. No connection attempts while there is no network or the device is not enrolled.
  - **Events** (`push_controller.dart`, renamed `DeviceEventController`): `consent_request { auth_req_id }` → fetch pending requests over the existing authenticated call and, when the app is not in the foreground, show a local notification with fixed text ("Approval requested" / "Open Biatec Authenticator to review"; `flutter_local_notifications`); `consent_closed { auth_req_id }` → drop that card and its notification; `device_enrolled { kid }` → refresh the device list and notify with fixed text; `settings_changed` → refresh settings. Payload values are ids only and are validated (`auth_req_id` charset and length) before use. The 5 second poll while the Approvals screen is open stays.
  - **Android "Stay connected for approvals"** (Settings, default off, with an explanation of the battery cost and of what happens when it is off: "you will see requests when you open the app"). When on: a foreground service with a persistent low-priority notification keeps the process and the channel alive (`flutter_foreground_task`, or a small Kotlin service if the plugin cannot host the channel). Declare the foreground service type the current Android documentation requires for this use and record in the README what Play Console declaration it needs. The connection code running under the service must still sign the ticket request with the presence key (register the keystore plugin for the background engine). If that cannot be made to work, stop and report in the Hand-off; do not weaken or skip the device proof. Offer the battery-optimisation exemption once; restart after boot only when the setting is on.
  - **iOS.** The channel runs while the app is in the foreground. Optional Apple wake behind `--dart-define=APNS_ENABLED=true` (default false): a small Swift method channel registers for remote notifications and returns the device token as hex, and the app calls `PUT /devices/push` with `{ provider: "apns", token }`. No Firebase. With the flag off no push entitlement or registration code runs.
  - **Mock** (`mock-oidc`): `POST /devices/hub-ticket` (single use, 30 seconds), a WebSocket endpoint `/hubs/device` in a new file `src/device-hub.ts` speaking the framing above (handshake, invocations, pings, close after a configurable lifetime), the four events sent when the mock's state changes, and `PUT /devices/push` answering `400 unsupported_push_provider` for `fcm`. The existing push outbox test helper keeps recording the events.
  - **Docs.** `apps/biatec_authenticator/README.md`: what wakes the phone and what does not. (`docs/required-changes-biatec-oidc.md` is corrected by T-240.)
  - English and Slovak strings.
- Out: the server (AlgorandGoogleDriveAccount issue 29); the two keys (T-241; until then the single device key signs the ticket request).

## Parallel work

Parallel work possible: **yes** (after T-240, which also changes the mock; T-241 waits for this task because both change the Android and iOS projects).

## Acceptance criteria

- [ ] `git grep -i "firebase\|fcm" apps/biatec_authenticator` finds nothing except the mock's refusal test and a changelog line.
- [ ] Dart tests against a local WebSocket test server: handshake, an invocation delivered to the controller, two messages in one frame, ping answered, close with and without reconnect, backoff sequence with a fake clock, a new ticket for every connection, an unknown target ignored, a malformed `auth_req_id` ignored.
- [ ] Controller tests: each event has the effect above; the notification text contains nothing from the payload.
- [ ] Contract test against the mock: a request opened in the mock arrives over the channel.
- [ ] Setting off → no service is started (test on the service starter fake); setting on → started, and stopped when the device is unenrolled.
- [ ] `flutter analyze` and `flutter test` pass; `./gradlew :app:compileDebugKotlin` compiles; `pnpm --filter mock-oidc test` passes.
- [ ] Manual checklist in the README for a person with an Android phone (request arrives with the app in the background and the setting on; nothing arrives with it off until the app is opened) and an iPhone (arrives in the foreground). The Hand-off says which were run.

## Implementation notes

- SignalR's JSON protocol is documented in the ASP.NET Core repository (`HubProtocol.md`); check the handshake and ping rules there rather than from memory.
- The ticket appears in the WebSocket URL. Never log the URL.
- Native code here has never run on a device (README lines 119 to 121); say so in the Hand-off and do not tick a device criterion from a unit test.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test && (cd android && ./gradlew :app:compileDebugKotlin) && cd ../.. && pnpm --filter mock-oidc test`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-13 09:05 · scholtz · (new) → blocked · created from revision 2 of the consent architecture record (AlgorandGoogleDriveAccount issue 23): no Firebase
- 2026-11-14 15:18 · kanban-board · blocked → backlog · blockers done: T-240
- 2026-11-14 16:30 · scholtz · backlog → in-progress · claimed by session-20261114-1630 on task/T-244
