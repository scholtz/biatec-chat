---
id: T-250
title: "Authenticator: a protected key store for Windows (TPM and Windows Hello), so a desktop build can enroll"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-248]
area: ["apps/biatec_authenticator/windows/runner/**", "apps/biatec_authenticator/lib/keys/**", "apps/biatec_authenticator/lib/lock/**", "apps/biatec_authenticator/lib/enrollment/**", "apps/biatec_authenticator/test/keys/**", "apps/biatec_authenticator/test/enrollment/**", "apps/biatec_authenticator/README.md", "docs/authenticator-windows-key-store.md", "apps/biatec_authenticator/windows/tools/**", "PRODUCT-DESCRIPTION.md"]
parallel: no
conflicts_with: [T-249, T-251]
issue: "#423"
branch: "task/T-250"
owner: "session-20261116-2350"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-16
updated: 2026-11-17
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

A Windows build enrolls a device whose keys are held by the computer's TPM, and an approval needs Windows Hello
each time.

## Design (owner's decision, 2026-11-17)

Both keys are **P-256 keys made in the TPM** (Microsoft Platform Crypto Provider, `NCrypt*`), not exportable, so the
protocol, the server and the backend verifier stay as they are: no new algorithm, no protocol version.

- **Presence key:** signs without a prompt, as on the phones.
- **Approval key:** the same kind of key. The prompt is the Windows Hello check (`UserConsentVerifier`, shown over
  the app's window) and it is part of the one native `signApproval` call: the key is not used when the check fails
  or is cancelled, and the Dart side has no way to sign with it without the check.
- **No software fallback.** No TPM, or Windows Hello not set up: refused with a text that says which, before the
  browser opens. A key the provider reports as not hardware is refused.
- **The weaker guarantee, accepted by the owner:** on a phone the hardware itself refuses to use the approval key
  without the biometric. Here the gesture is checked by the app's own code, so a program running as the same Windows
  user that bypasses the app (and the prompt) could ask the TPM to sign. The key still cannot be taken off the
  computer. `docs/authenticator-windows-key-store.md` says this plainly, so a bank can weigh it.
- The RS256 Windows Hello key (the earlier recommendation, which needed a protocol version) is not built.
- A desktop device may approve everything a phone may. The enrollment says `platform: windows`.

| Requirement (PSD2 RTS 2018/389) | What this gives |
| --- | --- |
| Possession that cannot be copied (art. 7) | Keys made in the TPM, not exportable. |
| Inherence or knowledge (art. 6, 8) | Windows Hello biometric or PIN, checked for every approval. |
| Independence of the elements (art. 9) | The keys live in the TPM; the gesture is not hardware-bound (above). |
| Dynamic linking (art. 5) | Unchanged: the signature covers the amount and the payee. |

## Implementation

1. `windows/runner`: C++ with CNG (`NCrypt`) for the keys and `IUserConsentVerifierInterop` for the prompt, on a
   worker thread (a prompt on the UI thread would stop the window), results posted back to the UI thread. The
   channel `io.biatec.authenticator/keystore` with `generateKeys`, `generatePresence`, `load`, `keysState`,
   `signPresence`, `signApproval`, `deleteKeys`, `probe` (new, Windows only; others fall back to `keysState`);
   the legacy methods answer "not found". The channel `io.biatec.authenticator/biometric` (`status`,
   `authenticate`, and `screenLocked` from the Windows session lock) so the app lock and the single-key approvals
   use Windows Hello.
2. Dart: `KeySecurityLevel.tpm`, `DeviceKeyStore.availability()`, the enrollment probe before the browser (no TPM,
   Windows Hello not set up), texts in English and Slovak, the key protection card.
3. Tests: Dart unit tests with a fake channel for every method and error mapping; the build; a manual checklist
   in `docs/authenticator-windows-key-store.md` (native code is not run by the test suite).

## Scope

- In: the above, the document, the README section, the PRODUCT-DESCRIPTION wording of the device keys on Windows.
- Out: Linux and macOS key stores; building and signing the MSIX; verification of a Windows attestation on the
  server (Windows has no counterpart of Play Integrity).

## Parallel work

Parallel work possible: **no**. It shares `lib/enrollment/**` with T-249 and most of `lib/**` with T-251.

## Acceptance criteria

- [ ] On a Windows computer with a TPM and Windows Hello the enrollment completes against the mock after the
      sign-in, and the key protection card says the keys are in the TPM (manual checklist).
- [ ] A computer without a TPM, or without Windows Hello, is refused before the browser opens, with a text that
      says which one is missing; no key is kept.
- [ ] An approval asks for Windows Hello, and a cancelled or failed prompt signs nothing.
- [ ] The private keys cannot be exported, and a key the provider does not report as hardware is refused.
- [ ] The app lock and the approvals of the single-key mode use Windows Hello on Windows.
- [ ] The weaker guarantee is written down in `docs/authenticator-windows-key-store.md` and PRODUCT-DESCRIPTION.
- [ ] `flutter build windows` builds; `flutter test` passes.

## Verification

    cd apps/biatec_authenticator
    flutter test
    flutter build windows

Then the manual checklist in `docs/authenticator-windows-key-store.md` on a Windows computer with a TPM and
Windows Hello.

## Hand-off

## Review

## Activity log

- 2026-11-16 12:00 · session-20261116-0900 · (new) → blocked · created as a follow-up of T-248, which it waits for
- 2026-11-16 11:00 · kanban-board · backlog → blocked · waiting on: T-248
- 2026-11-16 11:46 · kanban-board · blocked → backlog · blockers done: T-248
- 2026-11-16 14:30 · session-20261116-0900 · backlog → backlog · implementation plan added at the user's request (plan only, not started); area widened, so it now conflicts with T-249 and T-251
- 2026-11-16 15:00 · session-20261116-0900 · backlog → backlog · owner decided: RS256 Windows Hello approval key; a desktop device may approve everything
- 2026-11-16 23:50 · scholtz · taken · status stays backlog
- 2026-11-16 23:50 · scholtz · backlog → in-progress · claimed; branch task/T-250
- 2026-11-17 09:00 · scholtz · in-progress · owner decided: P-256 TPM keys with a Windows Hello prompt checked by the app (no protocol change); the RS256 design is dropped
