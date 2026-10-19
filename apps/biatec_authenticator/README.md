# biatec_authenticator

Flutter shell for Biatec Authenticator. Navigation and empty screens only; enrollment and approval logic are
separate tasks.

## Platform folders

This scaffold was written by hand without the Flutter SDK. Generate the platform folders once with:

    flutter create --platforms=android,ios --project-name biatec_authenticator --org io.biatec .

`flutter create .` keeps the existing `lib/`, `test/` and `pubspec.yaml`.

## Config

The Biatec OIDC base URL comes from `--dart-define=OIDC_BASE_URL=...` and defaults to the mock OIDC
(`http://10.0.2.2:5080/mock-oidc`, the host machine as seen from the Android emulator).

    flutter run --dart-define=OIDC_BASE_URL=https://oidc.example

Nothing serves the default mock URL yet; it is a placeholder until the authenticator enrollment and approval tasks
add an HTTP mock of Biatec OIDC. The app makes no network calls today.

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

## Checks

    flutter analyze && flutter test
