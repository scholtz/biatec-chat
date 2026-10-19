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

## Checks

    flutter analyze && flutter test
