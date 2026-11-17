# Authenticator on Windows: device keys in the TPM, approvals with Windows Hello or a PIN of the app (T-250)

A Windows build of Biatec Authenticator enrolls a device whose keys are held by the computer's TPM, and asks for Windows
Hello for every approval. On a computer where Windows Hello cannot be used (not set up, no device, switched off by a
policy, or a Remote Desktop session that cannot reach it) it asks for a PIN of the app instead, which the TPM checks.
This page says what is protected, what is not, how to check it, and what a bank should know.

## What is built

| | |
| --- | --- |
| Keys | Two P-256 keys (the approval key and the presence key), made in the **Microsoft Platform Crypto Provider**, the CNG provider that keeps keys in the TPM. They belong to the signed-in Windows user. |
| Export | Not possible. The provider keeps the private key in the TPM; no export policy is ever set, and the native code has no call that returns private key material, only calls that sign. |
| Algorithm | ES256 (ECDSA P-256 with SHA-256), signature as the 64-byte `r \|\| s`. Nothing changes in the protocol, the server or the backend verifier. |
| Approval key | The signature is made in one native call that first asks **Windows Hello** (face, fingerprint or the Hello PIN) over the app's window and signs only after Windows answers "verified". Cancelled, locked out and failed prompts sign nothing and are reported with the same codes as on a phone. |
| Presence key | Signs without a prompt, like the presence key on a phone. |
| App lock | The lock and the re-authentication before sensitive actions use the same Windows Hello check, and the app locks when Windows locks (`screenLocked`). |
| Without Windows Hello | The user chooses a **PIN of the app** (6 to 64 characters) at enrollment, in a native dialog, after the sign-in and before any key is made. It is the usage secret of a third TPM key (the lock key, `io.biatec.authenticator.lock.v1`) and, in two-key mode, of the approval key: the TPM refuses to sign without it and locks for a while after too many wrong tries. The app stores no copy and no hash of it, and it never crosses the channel to the Dart side. The app lock and every approval ask for it; "is this the PIN" is answered by a TPM signature with the lock key. Which check an enrollment uses is fixed when it is made (the lock key is there or not); a later change needs a new enrollment. A forgotten PIN cannot be recovered: the device is enrolled again. See "The PIN of the app" below. |
| Refused | No TPM (or off in the firmware): said **before the browser opens**, so a computer that cannot enroll is not sent through a sign-in. A key the provider does not report as hardware is refused and removed, as on a phone. A closed PIN dialog makes no keys and enrolls nothing. |
| Security level | `tpm`, read back from the key (`NCRYPT_IMPL_TYPE_PROPERTY` has the hardware flag), never assumed. |
| Reported as | The approval key is `appGated` (the app's own gate), **not** `authBound` (which says the hardware enforces the gesture, as on a phone). The app accepts either for approving (`DeviceKey.gestureRequired`) and keeps them apart. |
| New keys | Every enrollment deletes any old pair and makes new keys: a TPM key outlives an uninstall, and an old pair must not reach the server as a new device. |
| Single-key mode | Real Biatec OIDC has no two-key enrollment yet (`TWO_KEY_DEVICE` is off). Then only the presence key is made, in the TPM, and an approval is gated by the app lock's Windows Hello check, as before T-241 on a phone. |

Code: `apps/biatec_authenticator/windows/runner/` (`keystore.cpp` the TPM keys, `hello.cpp` the Windows Hello check,
`pin_dialog.cpp` the PIN dialog, `key_channels.cpp` the channels `io.biatec.authenticator/keystore` and `io.biatec.authenticator/biometric`), and
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
prove that the app is the real app. The device is named "Windows computer" in the device list (the enrollment sends no
platform field, and a server policy that treats desktop devices differently would need one, see the open decisions);
today a desktop device may approve what a phone may.

## The PIN of the app (added on 2026-11-17, when the owner's computer could not use Windows Hello)

What it gives, compared with the Windows Hello path:

- **Two-key mode:** the approval key itself needs the PIN, and the TPM enforces it with its own lockout. A program
  running as the same Windows user that asks the TPM directly is refused unless it knows the PIN. This is stronger than
  the app-checked Windows Hello prompt.
- **Single-key mode** (real Biatec OIDC today): the presence key signs the approval and has no PIN, so the PIN stands
  in front of it only through the app lock, like Windows Hello does. The same-user program is not stopped.
- **What it does not give:** the PIN is typed into a dialog of the app, in the user's session. A program that reads
  keystrokes in that session can learn it. It is knowledge, not inherence. The key is still reported as `appGated`,
  never `authBound`.
- **One check per enrollment:** an enrollment made with Windows Hello cannot be opened where Windows Hello cannot
  be reached later (for example over Remote Desktop), and the other way round an enrollment made with the PIN keeps
  asking for the PIN after Windows Hello is set up. Enroll again to change it.
- **Not verified:** the code was built but never run. In particular, how the Platform Crypto Provider answers a wrong
  PIN and a locked TPM is read from the documentation of CNG and the TPM: a status that is not recognised as a lockout
  is treated as a refused PIN, so an unknown answer never opens anything. Steps 10 to 13 of the checklist check it.

## What a bank can weigh (engineering advice, not a compliance certificate)

| Requirement (PSD2 RTS 2018/389) | What this gives |
| --- | --- |
| Possession that cannot be copied (art. 7) | Keys made in the TPM, not exportable. |
| Inherence or knowledge (art. 6, 8) | Windows Hello biometric or PIN, or the PIN of the app (knowledge), checked for every approval. |
| Independence of the elements (art. 9) | The keys live in the TPM, outside the app's process. The Windows Hello gesture is not hardware-bound (above); the PIN of the app is enforced by the TPM on the lock key and the two-key approval key, but is typed in the user's session. There is no app attestation. |
| Dynamic linking (art. 5) | Unchanged: the signature covers the amount and the payee. |

Ways to strengthen it later: the PIN of the app also for computers that have Windows Hello (the TPM then enforces the check on the approval key), a PIN on the single-key presence key once the server tells approvals from presence proofs, the RS256 Windows Hello key (hardware-bound gesture, needs a protocol version), a signed
MSIX package, an organisation policy that allows desktop devices only for approvals up to a limit.

## Manual checklist (NOT run: native code is not run by the test suite, and the build environment of this task could not run executables)

Run on a Windows 11 computer with a TPM 2.0 and Windows Hello set up. `flutter build windows`, then run
`build\windows\x64\runner\Release\biatec_authenticator.exe`.

1. **TPM and Hello probe.** On the connect step press the sign-in button: the browser opens (no message about a key
   store, a TPM or Windows Hello).
2. **No Hello.** In Windows Settings, Accounts, Sign-in options, remove Windows Hello (PIN) for the test user (or use a
   computer without it, or a Remote Desktop session). The browser still opens; steps 10 to 13 cover what follows.
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

Without Windows Hello (the PIN of the app):

10. **A new PIN.** Sign in. After the browser a dialog "Biatec Authenticator" asks for a new PIN twice. A PIN shorter
    than 6 characters, or two different ones, is refused in the dialog. Cancel: "No PIN was chosen, so nothing was
    enrolled", and `certutil` lists no key of the app. With a PIN: the enrollment completes, and `certutil` also lists
    `io.biatec.authenticator.lock.v1`.
11. **Unlock and approve.** Opening the app, the lock after Win+L, and every approval ask for the PIN in the same
    dialog. The right PIN opens or signs. A wrong PIN does neither. Cancel does neither.
12. **Lockout.** Enter wrong PINs until the TPM locks (the number depends on the TPM; Windows sets 32 by default, so use
    a test computer). Record what the app says before and at the lockout, and the status in the log if it is not
    "locked".
13. **Remove the device**: `certutil` lists none of the three keys. Enroll again: a new PIN is asked.

Record the result of each step (and the computer: Windows build, TPM manufacturer) in the hand-off of the task.

## Tests that do run

`test/keys/device_key_store_test.dart` pins every channel method, argument, level and error code the Dart side uses
against `key_channels.cpp` and `keystore.cpp` (and that the code makes P-256 keys in the TPM provider, never sets an
export flag, signs the approval only after the Windows Hello result "verified", and on the PIN path signs only with a
PIN the TPM accepted); `test/enrollment/desktop_login_test.dart`
covers the probe before the browser (no key store, no TPM, a probe that fails for another reason).
