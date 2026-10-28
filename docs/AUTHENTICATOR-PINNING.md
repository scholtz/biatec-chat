# Authenticator: TLS pinning for Biatec OIDC

The authenticator talks to Biatec OIDC with device tokens, binding codes and signed receipts. It must reach Biatec OIDC and nothing that merely
holds a certificate the phone happens to trust (an authority a user or administrator installed, or one that was breached). So the app pins the
**public keys** of Biatec OIDC's servers. This follows [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md), "Security Standard For The App", rule 3
(OWASP MASVS NETWORK-2).

## What is pinned, and where

| Layer | What it checks | Pins | Where |
| --- | --- | --- | --- |
| The Dart client (every call to Biatec OIDC) | The key of the **leaf** certificate the server presents, and its validity dates. The system's list of authorities is not used at all. | The server's own keys | `lib/security/pinning/`, table in `pins.dart`, or `--dart-define=OIDC_PINS=` |
| Android's own network stack (second line) | Any certificate in the chain | The issuing roots | `android/app/src/main/res/xml/network_security_config.xml` |
| iOS's own network stack (second line, iOS 14+) | Any CA in the chain | The issuing roots | `NSPinnedDomains` in `ios/Runner/Info.plist` |

Why two kinds: Dart's TLS stack (`dart:io`) shows an app only the leaf certificate, and it ignores Android's network security config and iOS's
`NSPinnedDomains`. So the Dart client pins the leaf key. The native files protect what the platform's own stack sends (and read as the second line
of defence), and pin roots, which do not change when a certificate is renewed.

A pin is `sha256/` and the Base64 of the SHA-256 of the certificate's SubjectPublicKeyInfo. There is no way to continue past a failed pin: the
request is not sent, the error says the connection could not be verified, and the only remedy is an app update with new pins.

Every HTTPS host needs pins, in every build. A host with none (including a mistyped one) stops the app at start with a message, so there is no
unpinned HTTPS. Plain `http` is for the mock on the developer's own machine in a debug build and nowhere else.

## Computing a pin

From a server:

```sh
HOST=stage.oidc.biatec.io
openssl s_client -connect $HOST:443 -servername $HOST </dev/null 2>/dev/null \
  | openssl x509 -pubkey -noout \
  | openssl pkey -pubin -outform DER \
  | openssl dgst -sha256 -binary | openssl enc -base64
```

From a certificate file: `openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | openssl enc -base64`.
From a private key (the way to make a backup pin before the key is used): `openssl pkey -in key.pem -pubout -outform DER | openssl dgst -sha256 -binary | openssl enc -base64`.

Pins in use on 2026-10-28:

| Host | Certificate | Pin |
| --- | --- | --- |
| `stage.oidc.biatec.io` | leaf (Let's Encrypt, 2026-09-26 to 2026-12-25) | `z8N92PhmB13Pcufe5VrnS1/Z14cMYcjKqhaQYdVJ1IM=` |
| | intermediate `YR2` | `nWN7PSep5XDQdge5zK24CnCRXHr3KvzhKEGxsdqCX9E=` (not pinned: intermediates change) |
| | root `ISRG Root YR` | `fk6IOKit1ild5647BH06ujSIq5XbCgqlbYl6ANhhi88=` |
| | `ISRG Root X1` | `C5+lpZ7tcVwmwQIMcRtPbsQtWLABXhQzejna0wHFr8M=` |
| | `ISRG Root X2` | `diGVwiVYbubAI3RW4hB9xU8e/CH2GnkuvVFZE8zmgzI=` |
| `oidc.biatec.io` | **no pin yet**: it serves the ingress controller's placeholder certificate | |

## What Biatec's operations must do

1. **Keep the leaf key across renewals.** Request the certificate with the key reused (cert-manager: `spec.privateKey.rotationPolicy: Never`). If a
   renewal changes the key, every installed app refuses to connect until it is updated. Certificate renewals themselves (every 60 to 90 days) are fine.
2. **Have a backup key before you need it.** Generate a second private key, store it offline, and compute its pin. A release build on iOS or Android
   needs at least two pins (the current key and the backup) and will not start with one.
3. **Production.** `oidc.biatec.io` must serve a real certificate before a production build can exist. Pass its pins with
   `--dart-define=OIDC_PINS=sha256/<current>,sha256/<backup>`; they replace the table in `pins.dart` for that build.

## Rotating a key

1. Generate the new key and compute its pin. If the backup key is the new key, skip to step 3.
2. Release an app version whose pins are `current, backup, new` (three). Wait until almost every installed app has it (see the app-version metric).
3. Move the server to the new key (`rotationPolicy` for one renewal, or a manual certificate).
4. Release an app version whose pins are `new, next backup`, and drop the old pin.

If the leaf key must change at once (a compromise), apps without the backup pin stop working until updated: that is the price of pinning, and the
reason the backup key is stored somewhere safe.

## The native files

- Android: the pins in `network_security_config.xml` carry an `expiration`. After that date Android stops enforcing them, so a test fails when
  it is less than 60 days away, and the date moves forward at each release. The debug build has its own file (`src/debug/res/xml`) that allows plain
  http for `10.0.2.2`, `localhost` and `127.0.0.1` only, and trusts user certificates for a debugging proxy; a release build never does.
- iOS: `NSPinnedDomains` lists the roots. There are no ATS exceptions (`NSAllowsArbitraryLoads` and `NSExceptionDomains` are absent).

## Tests

`test/pinning/` holds: the pin of each real stage certificate against `openssl`'s result; the policy (key, host, validity dates); the build rules
(mobile release needs two pins, unknown hosts do not start); a real TLS server on this machine with certificates made by `openssl` at test time,
showing a pinned key is trusted without any system trust, another key is refused before a request is sent, a pin for another host name does not
carry over, and an expired or not-yet-valid certificate is refused; and source checks that no other HTTP client exists in `lib/`.
`test/platform_hardening_test.dart` checks the Android and iOS files.
