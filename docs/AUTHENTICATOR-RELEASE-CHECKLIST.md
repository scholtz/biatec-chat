# Biatec Authenticator: signed release checklist

Work through this for every build that leaves the team. Tick each line in the release pull request or ticket. The automated parts run
in CI ([ci.yml](../.github/workflows/ci.yml), job `authenticator` and `authenticator-security`); the rest is by hand. Context:
[AUTHENTICATOR-SECURITY.md](AUTHENTICATOR-SECURITY.md).

## Before the build

- [ ] `main` is green: `flutter analyze`, `flutter test`, the hardening and pinning tests, the OSV scan, the lockfile check, the
      gates and the matrix check.
- [ ] `docs/AUTHENTICATOR-SECURITY.md`: no row got worse since the last release; every open item still has an owner or a task.
- [ ] Dependencies reviewed: the SBOM of the commit is attached to the release; nothing new was added without a reason.
- [ ] Pinned public keys are valid and the backup pin is not the one in use ([AUTHENTICATOR-PINNING.md](AUTHENTICATOR-PINNING.md)).
- [ ] `version` in `pubspec.yaml` is the release version, and `appVersion` in `lib/settings/app_info.dart` equals it (a test checks).
- [ ] The Biatec OIDC the build points at is the production one and its settings API is on (`CONSENT_SETTINGS_API`).

## The build

- [ ] Built in CI or on a clean machine from the release tag, never from a working copy.
- [ ] Android: `flutter build appbundle --release --obfuscate --split-debug-info=<folder> --dart-define=OIDC_BASE_URL=<production>
      --dart-define=RELEASE_CERT_SHA256=<digest of the release certificate>`. Without the digest the release build refuses to open.
- [ ] iOS: archived with the release provisioning profile; the App Attest capability is on the App ID and `Runner.entitlements` says
      `production`.
- [ ] Signed with the **release** key (Play App Signing upload key or the iOS distribution certificate), which exists only in the
      release secret store. Never the debug key, never a key in the repository, never in a CI log.
- [ ] The release certificate's SHA-256 was computed from the signed artifact (`apksigner verify --print-certs`) and is the value in
      `RELEASE_CERT_SHA256`.
- [ ] The debug symbols (`--split-debug-info` folder) are stored encrypted and kept for the life of the release.
- [ ] The build is not debuggable (`android:debuggable` absent in the merged manifest) and logs nothing (the hardening test, run with
      `REQUIRE_MERGED_MANIFEST=1`, passes on this build's merged manifest).

## Checks on the signed build, on devices (open item O-1)

- [ ] Fresh install on an Android phone with StrongBox, one without, and an iPhone: enroll, approve, add a two-factor account by QR,
      link and key, copy a code, lock and unlock, lose and re-enroll a biometric (the changed-biometrics screen shows).
- [ ] The camera permission is asked only when the scanner opens; no other permission prompt appears.
- [ ] Screenshots and screen recording show a blank screen on the secure area; the app switcher shows no data.
- [ ] A rooted or jailbroken phone, a debugger and Frida each close the app (and the same build opens on a healthy phone).
- [ ] The proxy test: a user-installed CA cannot intercept; the pinned connection fails closed on a wrong pin.
- [ ] No backup contains app data: `adb backup` yields nothing, and a device backup restored elsewhere has no vault or keys.
- [ ] The clipboard is cleared 30 seconds after a copy, and a clipboard preview shows the code hidden (Android 13 and later).

## Release

- [ ] The independent penetration test report has no open critical or high finding (open item O-2), or each is accepted in writing
      with an owner.
- [ ] Staged rollout started at a small percentage; crash and support channels watched. The authenticator has no analytics, so the
      stores' own crash reports and the support mailbox are the signal.
- [ ] The security contact for reports is published in the store listing and in the pen-test brief. (No contact is named yet:
      owner: user.)
- [ ] A way to withdraw: the previous version is kept, and Biatec OIDC can refuse a build that is too old (T-232).

## After the release

- [ ] Tag the release; attach the SBOM and the signed artifact's digest.
- [ ] Rotate nothing by accident: if a signing or pinning key was touched, record why and when.
