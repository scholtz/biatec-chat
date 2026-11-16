---
id: T-250
title: "Authenticator: a protected key store for Windows (TPM and Windows Hello), so a desktop build can enroll"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-248]
area: ["apps/biatec_authenticator/windows/runner/**", "apps/biatec_authenticator/lib/keys/**", "apps/biatec_authenticator/lib/lock/**", "apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/test/keys/**", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/README.md", "docs/authenticator-windows-key-store.md", "docs/required-changes-biatec-oidc.md", "PRODUCT-DESCRIPTION.md"]
parallel: no
conflicts_with: [T-249, T-251]
issue: "#423"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-16
---

# T-250 · Authenticator: a protected key store for Windows

## Context

Found while doing T-248, and confirmed by the user on 2026-11-16: a Windows build signs in with the system browser
against stage, and then the enrollment stops with "This device cannot be enrolled"
(`EnrollmentController.noKeyStoreError`).

Windows does have protected key storage. The app does not use it yet: the device keys are made by native code behind
the channel `io.biatec.authenticator/keystore`, which exists for Android (`KeystorePlugin.kt`) and iOS
(`KeystorePlugin.swift`) only. The app fails closed on a key that is not in hardware
(`KeySecurityLevel.isHardware`), and that rule stays.

PRODUCT-DESCRIPTION, "Authenticator App (Flutter)": two device keys in hardware, the approval key usable only after
the user's biometric for each signature.

What Windows offers:

| Building block | What it gives | Limit |
| --- | --- | --- |
| TPM 2.0 through CNG, "Microsoft Platform Crypto Provider" (`NCrypt*`) | P-256 keys made in the TPM, never exportable. Every Windows 11 computer has a TPM 2.0. | The provider does not ask for Windows Hello by itself. |
| Windows Hello key, `KeyCredentialManager` (WinRT) | A key per user and app; the system asks for face, fingerprint or the Hello PIN for **each** signature, and the TPM enforces it. `GetAttestationAsync` proves the key is in a TPM. | The key is RSA-2048 (RS256), not P-256. Without a TPM it silently falls back to software, so the attestation status must be checked. |
| `UserConsentVerifier` (WinRT) | A Windows Hello prompt. | Only a yes/no for the app. It is not bound to a key, so a program running as the user can skip it. |
| DPAPI, Credential Manager | Encrypts data for the signed-in user. | Software only. Any program running as the user reads it. Not acceptable for a device key. |

## Goal

A Windows build enrolls a device whose keys are held by the computer's TPM, an approval needs Windows Hello for each
signature, and the server can tell a desktop device from a phone.

## Recommended design

1. **Presence key:** P-256 in the TPM (Platform Crypto Provider), not exportable, no prompt. Same algorithm as on
   the phones, so nothing changes on the server.
2. **Approval key:** a Windows Hello key (`KeyCredentialManager.RequestCreateAsync`, signing with
   `RequestSignAsync`). This is the only Windows mechanism where the hardware, not the app, enforces "one user
   gesture for one signature", which is what the product description asks of the approval key. It is RSA-2048, so
   the server must accept an RS256 approval key for a device (a required change in Biatec OIDC, see step 2 of the
   plan).
3. **No software fallback.** No TPM 2.0, Windows Hello not set up, or a Hello key whose attestation does not say
   TPM: the security level is reported as `software` and the app refuses, with a text that says what to turn on.
4. **Device class.** The enrollment says `platform: windows`. Windows has no counterpart of Play Integrity or App
   Attest, so the server cannot prove the app is genuine. The server should treat a desktop device as a lower class
   (limits, or login approvals only). That gate is server-side, as every tier gate is.

The rejected alternative: a P-256 TPM key for approvals with a `UserConsentVerifier` prompt in front of it. It needs
no server change, but the gesture is checked by the app and not by the hardware. It is the fallback only if the
owner prefers no server change, and then only for a device class with low limits.

What a bank will ask, and the answer of this design (this is engineering advice, not a compliance certificate):

| Requirement (PSD2 RTS on strong customer authentication, 2018/389) | Answer |
| --- | --- |
| Possession element that cannot be copied (art. 7) | Keys made in the TPM, not exportable; TPM attestation of the Hello key at enrollment. |
| Inherence or knowledge element (art. 6, 8) | Windows Hello biometric, or the Hello PIN, which the TPM guards against guessing. |
| Independence of the elements on a multi-purpose device (art. 9) | Keys live in the TPM, outside the app's process. Weak point: no proof of app integrity on Windows. Mitigations: a signed MSIX package, the lower device class of point 4. |
| Dynamic linking (art. 5) | Unchanged: the approval signature covers the amount and the payee, as on the phones. |

## Implementation plan

1. **Spike (half a day, before any other step).** A small C++/WinRT program on a Windows 11 computer with a TPM that
   (a) makes a P-256 key in the Platform Crypto Provider and signs, (b) makes a Hello key, reads its attestation
   and signs, (c) confirms the prompt appears for each signature and can carry the app's window as its parent.
   Also try a P-256 key in the "Microsoft Passport Key Storage Provider" through `NCrypt`: if that works with a
   gesture for each signature, the approval key stays P-256 and step 2 is not needed. Write the result into
   `docs/authenticator-windows-key-store.md`.
2. **Server contract.** Record in `docs/required-changes-biatec-oidc.md`: an RS256 approval key at `POST /devices`
   and in the approval signature check, the `platform` of a device, the device class policy, and (optional) the
   check of the Windows attestation. Add the same to the mock in this repository so the tests run. Against real
   Biatec OIDC the Windows enrollment stays off by configuration until the server has it (AGENTS.md).
3. **Native key store.** `windows/runner/keystore_plugin.{h,cpp}` on the channel `io.biatec.authenticator/keystore`,
   with the methods the Dart side already calls: `generateKeys`, `generatePresence`, `load`, `keysState`,
   `signPresence`, `signApproval`, `deleteKeys`, `delete` (the legacy methods answer "not found"). Keys are named
   per user and per provider. ES256 signatures are returned as raw `r||s`, as on the phones. A cancelled prompt maps
   to `KeyPromptCancelled`, a removed Hello key to `ApprovalKeyInvalidated`. Work runs off the UI thread.
4. **Dart side.** `KeySecurityLevel.tpm` (hardware); the public key model learns RSA (`kty: RSA`, its RFC 7638
   thumbprint); `signApproval` reports its algorithm. Nothing changes for Android and iOS.
5. **Enrollment.** Probe the key store before the browser opens, so a computer that cannot enroll says so without a
   sign-in, with separate texts for "no TPM" and "Windows Hello is not set up" (English and Slovak). Keep the
   fresh-login rule: a desktop device is not enrolled on a login that may come from an existing browser session.
6. **App lock.** The lock of the secure area uses Windows Hello (`UserConsentVerifier` is enough here: it guards
   the screen, not a key).
7. **Packaging.** Note in the README what a release needs: a signed MSIX. Building it is a follow-up task.
8. **Tests and documents.** Dart unit tests with a fake channel (both key kinds, every error mapping, the probe);
   the manual checklist; README section; PRODUCT-DESCRIPTION if the approval key rule needs the Windows wording.

## Scope

- In: the eight steps above.
- Out: Linux and macOS key stores; building and signing the MSIX; the server changes themselves (other repository);
  verification of the Windows attestation on the server.

## Parallel work

Parallel work possible: **no**. It shares `lib/enrollment/**` with T-249 and most of `lib/**` with T-251.

## Acceptance criteria

- [ ] The spike's result is written down, and the approval key design follows it.
- [ ] On a Windows computer with a TPM the enrollment completes against the mock after the sign-in, and the key
      protection card says the keys are in the TPM.
- [ ] A computer without a TPM, or without Windows Hello, is refused before the browser opens, with a text that
      says which one is missing; no key is kept.
- [ ] An approval asks for Windows Hello for each signature, and a cancelled prompt signs nothing.
- [ ] The private keys cannot be exported (checked against the provider's key properties), and a Hello key without
      a TPM attestation is refused.
- [ ] A desktop device is not enrolled on a login that may come from an existing browser session: either the server
      proves the login fresh (`auth_time`, the in-app login) or the enrollment stays off, and the hand-off says which.
- [ ] `docs/required-changes-biatec-oidc.md` holds the server changes, and the Windows enrollment is off by
      configuration against a server that lacks them.
- [ ] `flutter build windows` builds; `flutter test` passes.

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

Then the manual checklist on a Windows computer with Windows Hello set up.

## Open decisions for the owner

1. RS256 approval key on Windows (recommended, needs the server change), or P-256 with an app-level Hello prompt
   (no server change, weaker)?
2. What may a desktop device approve: everything, lower limits, or logins only?

## Hand-off

## Review

## Activity log

- 2026-11-16 12:00 · session-20261116-0900 · (new) → blocked · created as a follow-up of T-248, which it waits for
- 2026-11-16 11:00 · kanban-board · backlog → blocked · waiting on: T-248
- 2026-11-16 11:46 · kanban-board · blocked → backlog · blockers done: T-248
- 2026-11-16 14:30 · session-20261116-0900 · backlog → backlog · implementation plan added at the user's request (plan only, not started); area widened, so it now conflicts with T-249 and T-251
