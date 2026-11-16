# Authenticator on Windows: device keys in the TPM, approvals with Windows Hello (T-250)

A Windows build of Biatec Authenticator enrolls a device whose keys are held by the computer's TPM, and asks for Windows
Hello for every approval. This page says what is protected, what is not, how to check it, and what a bank should know.

## What is built

| | |
| --- | --- |
| Keys | Two P-256 keys (the approval key and the presence key), made in the **Microsoft Platform Crypto Provider**, the CNG provider that keeps keys in the TPM. They belong to the signed-in Windows user. |
| Export | Not possible. The provider keeps the private key in the TPM; no export policy is ever set, and the native code has no call that returns private key material, only calls that sign. |
| Algorithm | ES256 (ECDSA P-256 with SHA-256), signature as the 64-byte `r \|\| s`. Nothing changes in the protocol, the server or the backend verifier. |
| Approval key | The signature is made in one native call that first asks **Windows Hello** (face, fingerprint or the Hello PIN) over the app's window and signs only after Windows answers "verified". Cancelled, locked out and failed prompts sign nothing and are reported with the same codes as on a phone. |
| Presence key | Signs without a prompt, like the presence key on a phone. |
| App lock | The lock and the re-authentication before sensitive actions use the same Windows Hello check, and the app locks when Windows locks (`screenLocked`). |
| Refused | No TPM (or off in the firmware), or Windows Hello not set up: said **before the browser opens**, so a computer that cannot enroll is not sent through a sign-in. A key the provider does not report as hardware is refused and removed, as on a phone. |
| Security level | `tpm`, read back from the key (`NCRYPT_IMPL_TYPE_PROPERTY` has the hardware flag), never assumed. |
| Single-key mode | Real Biatec OIDC has no two-key enrollment yet (`TWO_KEY_DEVICE` is off). Then only the presence key is made, in the TPM, and an approval is gated by the app lock's Windows Hello check, as before T-241 on a phone. |

Code: `apps/biatec_authenticator/windows/runner/` (`keystore.cpp` the TPM keys, `hello.cpp` the Windows Hello check,
`key_channels.cpp` the channels `io.biatec.authenticator/keystore` and `io.biatec.authenticator/biometric`), and
`lib/keys/device_key_store.dart` (`KeyStoreStatus`, `PlatformDeviceKeyStore.status`).

## The weaker guarantee (decided by the owner on 2026-11-17)

On a phone the hardware itself refuses to use the approval key without the biometric: the key is bound to the
authentication in the Secure Enclave or StrongBox. **On Windows the Windows Hello check is made by the app's own code
in front of the TPM key. Windows does not bind it to the key.** A program that runs as the same Windows user and
bypasses the app (it asks the TPM to sign with the key directly) is not stopped by a missing Hello check. It still
cannot take the key off the computer, and it can sign only on that computer while that user is signed in.

Why it was chosen: a Windows Hello key that the hardware does bind (`KeyCredentialManager`) is an RSA key, so it needs
an RS256 approval key in the protocol, the schemas, the test vectors, the mock, the conformance kit and the backend
verifier. The owner decided against that change and for a P-256 TPM key with an app-checked prompt.

The same applies to platform integrity: Windows has nothing like Play Integrity or App Attest, so the server cannot
prove that the app is the real app. The device enrolls with `platform: windows`, which keeps a later server policy
possible; today a desktop device may approve what a phone may.

## What a bank can weigh (engineering advice, not a compliance certificate)

| Requirement (PSD2 RTS 2018/389) | What this gives |
| --- | --- |
| Possession that cannot be copied (art. 7) | Keys made in the TPM, not exportable. |
| Inherence or knowledge (art. 6, 8) | Windows Hello biometric or PIN, checked for every approval. |
| Independence of the elements (art. 9) | The keys live in the TPM, outside the app's process. The gesture is not hardware-bound (above), and there is no app attestation. |
| Dynamic linking (art. 5) | Unchanged: the signature covers the amount and the payee. |

Ways to strengthen it later: the RS256 Windows Hello key (hardware-bound gesture, needs a protocol version), a signed
MSIX package, an organisation policy that allows desktop devices only for approvals up to a limit.

## Manual checklist (NOT run: native code is not run by the test suite, and the build environment of this task could not run executables)

Run on a Windows 11 computer with a TPM 2.0 and Windows Hello set up. `flutter build windows`, then run
`build\windows\x64\runner\Release\biatec_authenticator.exe`.

1. **TPM and Hello probe.** On the connect step press the sign-in button: the browser opens (no message about a key
   store, a TPM or Windows Hello).
2. **No Hello.** In Windows Settings, Accounts, Sign-in options, remove Windows Hello (PIN) for the test user (or use a
   computer without it). The sign-in button now shows "Set up Windows Hello first" and the browser does not open.
3. **Enrollment against stage** (single-key mode: real Biatec OIDC has no two-key enrollment yet, so only the presence
   key is made and there is no Windows Hello prompt at enrollment): sign in; the enrollment completes and the key
   protection card says "TPM security chip" and that the key cannot be exported. The two-key mode (approval key behind
   Windows Hello, `--dart-define=TWO_KEY_DEVICE=true`) needs a server that has it; a desktop build cannot reach the mock
   (T-248), so that path is covered by the Dart tests with a fake channel only, and by this checklist when such a
   server exists: then enrollment asks for Windows Hello once (the enrollment proof).
4. **Where the keys are.** In a command prompt: `certutil -csp "Microsoft Platform Crypto Provider" -key`. It lists
   `io.biatec.authenticator.approval.v1` and `io.biatec.authenticator.presence.v1`. The keys show no export flag.
5. **An approval** asks for Windows Hello over the app window (single-key mode: the app lock's check; two-key mode: the
   approval key's own check, made inside the one signing call). Cancel it: nothing is signed, the request stays
   pending. Fail it until Windows locks it: "locked", nothing is signed.
6. **Deny** does not ask for Windows Hello in two-key mode (presence key).
7. **Lock.** Lock Windows (Win+L) and unlock it: the app asks for Windows Hello. Turn the app lock on in Settings and
   check that opening the app asks for it.
8. **Remove the device** in the app, then `certutil -csp "Microsoft Platform Crypto Provider" -key` lists neither key.
9. **No TPM.** Disable the TPM in a virtual machine without one: the sign-in button shows "No security chip found" and
   no key is made.

Record the result of each step (and the computer: Windows build, TPM manufacturer) in the hand-off of the task.

## Tests that do run

`test/keys/device_key_store_test.dart` pins every channel method, argument, level and error code the Dart side uses
against `key_channels.cpp` and `keystore.cpp` (and that the code makes P-256 keys in the TPM provider, never sets an
export flag, and signs the approval only after the Windows Hello result "verified"); `test/enrollment/desktop_login_test.dart`
covers the probe before the browser (no key store, no TPM, no Windows Hello, a probe that fails for another reason).
`windows/tools/tpm_spike.cpp` is the small program that was written to try the provider by hand.
