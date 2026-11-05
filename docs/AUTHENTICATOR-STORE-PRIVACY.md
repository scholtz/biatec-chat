# Biatec Authenticator: answers for the store privacy forms

The text to enter in the Google Play **Data safety** form and the Apple **App Privacy** ("nutrition label") questions, kept here so
it matches the app. The app's own account of the same facts is the Privacy screen (`lib/privacy/storage_inventory.dart`); a test fails
when the app stores something that screen does not list. Change this file in the same pull request as any change to what the app
collects or sends. Context: [AUTHENTICATOR-SECURITY.md](AUTHENTICATOR-SECURITY.md) (MASVS-PRIVACY).

## The short version

The app has **no analytics, no advertising, no tracking and no third-party SDK except push messaging** (Firebase Cloud Messaging on
Android, APNs through Firebase on iOS). It does not collect location, contacts, photos, files, health data or an advertising identifier.
The camera is used only while the user scans a QR code, and no picture is kept. Two-factor secrets and codes never leave the phone.

## What leaves the phone

| Data | Sent to | Why | Linked to the user | Optional |
| --- | --- | --- | --- | --- |
| Device public key and, where the phone offers it, an attestation | Biatec OIDC | Enroll the device and prove it is the real app | Yes: the device belongs to the user's Biatec account | No: it is the product |
| Device token and a signature made with the device key | Biatec OIDC | Authenticate each device call | Yes | No |
| The user's approval or denial (a signed receipt), and security-setting changes | Biatec OIDC | The decision is the product | Yes | No |
| The push token | Biatec OIDC, and the push service (Google or Apple) | Tell the phone that a request is waiting | Yes (pseudonymous) | Yes: without it the app polls when open |
| A notice that something is waiting (no amount, name or code) | The push service | Delivery | No | Same as above |

Nothing else is sent. Crash reports are the store's own (opt-in by the user on the device); the app adds none.

## Google Play: Data safety

- **Does the app collect or share any of the required user data types?** Yes (device or other IDs: the push token and the device
  identifier).
- **Data types collected:** *Device or other IDs* (device token, push token). Purpose: *App functionality*, *Account management*.
  Not shared with third parties for their own use (the push service only delivers). Collection is required for the approval
  feature; the push token is optional.
- **Personal info, financial info, location, messages, photos, files, contacts, calendar, audio, health, web browsing, app activity:**
  not collected. (The amounts and recipients the user approves are held by Biatec OIDC as part of the user's own account, not
  collected by the app.)
- **Is all of the data encrypted in transit?** Yes (TLS 1.2 or later, certificate pinned).
- **Can users request that data is deleted?** Yes: *Erase everything on this phone* in Settings > About > Privacy removes everything
  the app holds, and the device can be cancelled from the account at Biatec OIDC, which removes the server record.
- **Security practices:** data encrypted in transit; independent security review (once the penetration test is done, see the
  release checklist).

## Apple: App Privacy

- **Data used to track you:** none.
- **Data linked to you:** *Identifiers > Device ID* (the device identifier and token), for *App functionality*.
- **Data not linked to you:** none.
- **Data not collected:** everything else (contact info, health and fitness, financial info, location, sensitive info, contacts,
  user content, browsing history, search history, usage data, diagnostics, purchases).
- The privacy manifest (`ios/Runner/PrivacyInfo.xcprivacy`) says the same: no tracking, no tracking domains, and the required-reason
  API uses of the app.
- The Face ID and camera usage descriptions are in `Info.plist`.

## Before each release

- [ ] Compare the Privacy screen, this file and the store forms. They must say the same thing.
- [ ] A new SDK or permission means a new row here first.
