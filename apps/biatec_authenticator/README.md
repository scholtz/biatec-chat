# biatec_authenticator

Biatec Authenticator (Flutter): device enrollment with a hardware key, consent approvals with signed receipts, push
notifications, and device, trusted-client and security settings screens. It runs against the mock Biatec OIDC
(`packages/consent-protocol/mock-oidc`); real Biatec OIDC has no consent or device APIs yet
([docs/required-changes-biatec-oidc.md](../../docs/required-changes-biatec-oidc.md)).

## Platform folders

This scaffold was written by hand without the Flutter SDK. Generate the platform folders once with:

    flutter create --platforms=android,ios --project-name biatec_authenticator --org io.biatec .

`flutter create .` keeps the existing `lib/`, `test/` and `pubspec.yaml`.

The `windows/` folder is checked in. Build it with `flutter build windows` (needs Visual Studio with the "Desktop
development with C++" workload and Windows Developer Mode for plugin symlinks). `windows/CMakeLists.txt` defines
`_SILENCE_EXPERIMENTAL_COROUTINE_DEPRECATION_WARNINGS` because `local_auth_windows` fails on MSVC 14.51+ without it.

## Config

The Biatec OIDC base URL comes from `--dart-define=OIDC_BASE_URL=...` and defaults to the mock OIDC
(`http://10.0.2.2:5080/mock-oidc`, the host machine as seen from the Android emulator).

    flutter run --dart-define=OIDC_BASE_URL=https://oidc.example

The default does not match where the mock actually listens: `pnpm --filter mock-oidc start` serves
`http://127.0.0.1:8089` (`PORT`, `HOST` override it) with no `/mock-oidc` path. From the Android emulator pass
`--dart-define=OIDC_BASE_URL=http://10.0.2.2:8089` (start the mock with `HOST=0.0.0.0`). An explicit URL that is not the
default counts as a real Biatec OIDC, so the settings screens are off unless `--dart-define=CONSENT_SETTINGS_API=true`
(see T-084 below).

## Enrollment and device key (T-051)

- `lib/keys/device_key_store.dart`: `DeviceKeyStore` is the hardware key (no export method, only `sign`).
  `PlatformDeviceKeyStore` calls the method channel `io.biatec.authenticator/keystore` (`generate`, `load`, `sign`,
  `delete`, each with an `alias`; `generate`/`load` return `{x, y}` base64url P-256). The native side is in
  `native/android/KeystorePlugin.kt` (Android Keystore, StrongBox with fallback to the TEE) and
  `native/ios/KeystorePlugin.swift` (Secure Enclave, `kSecAttrTokenIDSecureEnclave`). Wire them in after
  `flutter create`:
  Android: copy the Kotlin file to `android/app/src/main/kotlin/io/biatec/authenticator/` and call
  `flutterEngine.plugins.add(KeystorePlugin(applicationContext))` in `MainActivity.configureFlutterEngine`.
  iOS: add the Swift file to the Runner target and call
  `KeystorePlugin.register(with: self.registrar(forPlugin: "KeystorePlugin")!)` in `AppDelegate`.
- `lib/enrollment/`: `EnrollmentController` state machine (`notEnrolled`, `generatingKey`, `enrolling`, `inactive`,
  `activeRecoveryPending`, `active`, `failed`), `OidcEnrollmentClient` (`POST /devices`), secure-storage record, and
  the enrollment and recovery-code screen. Attestation is stubbed by the mock (not sent).
- The mock `POST /devices` takes a `login-*` bearer token (`login-alice` is prefilled against the mock) and
  returns `active_at` in epoch seconds. The kid is a random per-install alias (`biatec-authenticator-<hex>`),
  persisted before the key is created and used as both keystore alias and kid, because the server rejects a kid it
  has already seen.
- Recovery code (rule 7): never part of the enrollment response and never stored on the device. Once the device is
  active the app calls `POST /devices/recovery-code` (`Authorization: Device <token>`, refused with 403 while
  inactive), holds the code in memory while it is shown, and confirms with `POST /devices/recovery-code/ack`. These
  two calls are a fallback implemented in the mock; the needed change is in
  `docs/required-changes-biatec-oidc.md` section 4.
- Known gap: the native code was written without the Flutter, Android or Xcode toolchains and has NOT been compiled
  or run on a device or emulator. "Key is non-exportable on device" and the emulator enrollment run are therefore
  unverified and the task is partial until someone runs them (the iOS simulator has no Secure Enclave; use a device).
- Contract test against the real mock: start `pnpm --filter mock-oidc start`, then
  `flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/enrollment/mock_oidc_contract_test.dart`
  (skipped otherwise).

## Approvals and receipts (T-052)

- `lib/approvals/`: `ApprovalClient` (`GET /devices/requests`, `GET /consent/requests/{id}`,
  `POST /consent/requests/{id}/receipt`, all `Authorization: Device <token>`), `ApprovalController` (one request),
  the card, the pending list (polls every 5 s, and refreshes at once on a push) and the local history (secure storage, last
  100 decisions with their full receipts).
- The card is rendered only from the fetched consent request. The receipt signs `authorization_details` exactly as
  fetched, canonicalized with `jcs.dart` (RFC 8785, checked against `packages/consent-protocol/vectors`), with ES256
  by the device key, and carries the enrollment attestation (now kept in the enrollment record; a record saved before
  this change has none and must re-enroll).
- Approve: six-digit binding code, biometric check (`local_auth`, biometric only, fails closed), then a fresh fetch
  of the request. If that fetch fails the device is offline and nothing is signed; if the request differs from the
  card, the new one is shown and nothing is signed. Biatec OIDC counts the three attempts; after the third wrong
  code the app signs and posts a `code_failed` receipt.
- Deny is signed too, needs neither code nor biometric check, and signs the last typed code or the empty string.
- Unverified clients: warning banner with fingerprint only; their `binding_message` is never rendered or signed.
- `local_auth` needs `FlutterFragmentActivity` as the Android `MainActivity` and `NSFaceIDUsageDescription` in the
  iOS `Info.plist` once the platform folders exist.
- Contract test against the real mock (it verifies signature and attestation with its JWKS): start the mock, then
  `flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/approvals/mock_oidc_approval_contract_test.dart`.
  On Node older than 22.18 start it with `node --experimental-strip-types src/server.ts`.

## Push notifications (T-053)

- `lib/push/`: `PushController` keeps the push token registered at Biatec OIDC (`PUT /devices/push`,
  `Authorization: Device <token>`) on start, on token rotation, on app resume and on navigation, re-sent at most every 10 minutes; a tapped
  notification opens the approval screen for its `auth_req_id` (not a second time if it is already open), and a push
  received in the foreground refreshes the pending list and shows an in-app "New approval request" notice. The payload is read only for `auth_req_id` (`push_payload.dart`); the request itself is always
  fetched from Biatec OIDC, so a forged or altered push can at most open a request the device's user really has.
- FCM through `firebase_messaging` (`FcmPushTransport`, token = the Firebase Installation ID from `register()`).
  Push is off unless all four Firebase options are passed, so nothing per deployment is committed:

      flutter run --dart-define=FIREBASE_API_KEY=... --dart-define=FIREBASE_APP_ID=... \
        --dart-define=FIREBASE_SENDER_ID=... --dart-define=FIREBASE_PROJECT_ID=...

  Without them the app polls exactly as before. Android also needs the Google services Gradle setup and iOS the
  push capability once the platform folders exist.
- The mock never calls FCM; it records what it would send. Every new request queues one message per active device
  with a token, data `{ "auth_req_id": "..." }` only (`GET /mock/push-outbox?kid=...`). Contract test:
  `flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/push/mock_oidc_push_contract_test.dart`.

## Checks

    flutter analyze && flutter test

CI runs both (`ci.yml`, job `authenticator`) on every pull request. The `mock_oidc*_contract_test.dart` files (enrollment, approvals, push,
settings) are skipped there, because CI starts no mock.

## Devices, trusted clients and security settings (T-084)

- `lib/devices/`, `lib/clients/`, `lib/security/`: the three screens, one `SettingsController` (`lib/security/settings_controller.dart`)
  and `SettingsClient` (`GET /devices`, `POST /devices/{kid}/cancel`, `GET` and `PUT /devices/settings`, all with
  `Authorization: Device <token>`).
- Tightening (lower a threshold, turn a requirement on) applies at once. Loosening (raise a threshold, turn biometric, blocking or
  always-ask off, lift a client pause) first passes the biometric check, then the device key signs a `biatec_settings_change`
  (`isLoosening` decides). Biatec OIDC verifies it; an inactive device cannot loosen.
- Biatec OIDC has none of these APIs yet (`docs/required-changes-biatec-oidc.md` rows 21 and 22, fallback none). `AppConfig.settingsApi` is on
  for the mock URL and off otherwise; override with `--dart-define=CONSENT_SETTINGS_API=true`. When off, the screens say "Not available yet".
- Tests: `flutter test test/settings` (scripted OIDC); `test/settings/mock_oidc_settings_contract_test.dart` runs against a started mock
  (`MOCK_OIDC_URL`, fresh mock). The mock's side is tested by `pnpm --filter mock-oidc test`.
- `flutter analyze` and `flutter test` run in CI (see "Checks"); the contract test needs a started mock and is skipped there.
