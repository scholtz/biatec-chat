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
| Without Windows Hello | The user chooses a **PIN of the app** (6 to 64 characters) at enrollment, in a dialog of the app (`lib/security/pin_prompt.dart`, in the app's own look and language), after the sign-in and before any key is made. It is the usage secret of a third TPM key (the lock key, `io.biatec.authenticator.lock.v1`) and, in two-key mode, of the approval key: the TPM refuses to sign without it and locks for a while after too many wrong tries. The app stores no copy and no hash of it: it goes with the one channel call that needs it, the native copy is wiped after the call, and the Dart copy lives until the Dart memory is collected. The app lock and every approval ask for it; "is this the PIN" is answered by a TPM signature with the lock key. Which check an enrollment uses is fixed when it is made (the lock key is there or not); a later change needs a new enrollment. A forgotten PIN cannot be recovered: the device is enrolled again. See "The PIN of the app" below. |
| Without a TPM | Not refused (owner's decision, 2026-11-17, see "A computer without a usable TPM" below): the same keys are made in the software key store of Windows, the level is `windows_account`, and the app warns the user and says what would make the computer safer. |
| Refused | A key of the TPM that the provider does not report as hardware, and a software key that can be exported, are refused and removed. A closed PIN dialog makes no keys and enrolls nothing. |
| Security level | `tpm` when the Platform Crypto Provider says it is hardware (`NCRYPT_IMPL_TYPE_PROPERTY` of the provider has the hardware flag) and the key's export policy allows nothing; read back, never assumed. `windows_account` for a key of the software store. |
| Reported as | The approval key is `appGated` (the app's own gate), **not** `authBound` (which says the hardware enforces the gesture, as on a phone). The app accepts either for approving (`DeviceKey.gestureRequired`) and keeps them apart. |
| New keys | Every enrollment deletes any old pair and makes new keys: a TPM key outlives an uninstall, and an old pair must not reach the server as a new device. |
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
prove that the app is the real app. The device is named "Windows computer" in the device list (the enrollment sends no
platform field, and a server policy that treats desktop devices differently would need one, see the open decisions);
today a desktop device may approve what a phone may.

## A computer without a usable TPM (added on 2026-11-17, at the owner's request)

The owner asked that the app can be used on a Windows computer also when secure hardware is not available, and that
the user is told that the computer may not be the safest one and what can be improved.

- **When.** The TPM is used whenever it can be: the Platform Crypto Provider opens, says it is hardware, and makes a
  P-256 key (a key without a name, not kept, is made as the test). Otherwise (no TPM, off in the firmware, a TPM 1.2)
  the keys are made in the **Microsoft Software Key Storage Provider**.
- **What that gives.** The same P-256 keys under the same names, marked not exportable (the export policy is read
  back, and a key that could be exported is refused). Windows encrypts them for the Windows account (DPAPI).
- **What it does not give.** They are software. A program that runs as the user can sign with them, without any
  prompt; someone who can sign in as the user, or who has the disk and the user's password, can too. Nothing here is
  hardware-bound.
- **The user's check** is the same as with a TPM: Windows Hello when it can be used, otherwise the PIN of the app.
  The software provider cannot hold a PIN silently (it takes one only with a dialog of its own), so the PIN is checked
  by the app's native code: a random salt and a PBKDF2-HMAC-SHA256 hash (600,000 rounds) are kept in
  `%LOCALAPPDATA%\io.biatec.authenticator\pin.v1`, encrypted for the Windows account. After 5 wrong tries in a row
  the PIN is refused for 30 seconds, doubled with every further wrong try, up to an hour. This stops a person at the
  keyboard. It does not stop a program that runs as the user, which can read the file and try PINs on its own, or use
  the keys without the PIN.
- **What the user sees.** The key protection card (after the enrollment, and on the Devices screen) is a warning, not
  a success: "This computer is not the safest place for approvals", why, and a list: turn on the TPM in the firmware
  and enroll again, set up Windows Hello, turn on BitLocker, a strong Windows password and a locked screen, keep
  Windows up to date, approve larger payments on a phone. Settings says "Kept by your Windows account (software)".
  English and Slovak.
- **What the server sees.** Nothing different: Windows sends no attestation in either case, so Biatec OIDC cannot
  tell a TPM key from a software key of a Windows device. A relying party that needs hardware keys has to refuse
  Windows devices as a whole, or wait for a level in the enrollment (not in this task).
- **A change of store.** Which store an enrollment uses is fixed when it is made. A computer whose TPM is turned on
  later keeps its software keys until the device is removed and enrolled again (the warning says so).

## The PIN of the app (added on 2026-11-17, when the owner's computer could not use Windows Hello)

What it gives, compared with the Windows Hello path:

- **Two-key mode:** the approval key itself needs the PIN, and the TPM enforces it with its own lockout. A program
  running as the same Windows user that asks the TPM directly is refused unless it knows the PIN. This is stronger than
  the app-checked Windows Hello prompt.
- **Single-key mode** (real Biatec OIDC today): the presence key signs the approval and has no PIN, so the PIN stands
  in front of it only through the app lock, like Windows Hello does. The same-user program is not stopped.
- **What it does not give:** the PIN is typed into a dialog of the app, in the user's session, and passes through the
  app's memory. A program that reads keystrokes or the app's memory in that session can learn it. It is knowledge, not inherence. The key is still reported as `appGated`,
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

## What was run on a real computer (2026-11-17)

`apps/biatec_authenticator/windows/test/keystore_test.cpp` compiles the real `keystore.cpp` into a console program
(the command is at the top of the file). It was run on the owner's computer (Windows 11, a TPM 2.0, in a Remote
Desktop session) in both forms, and every check passed:

- **TPM:** new keys with and without a PIN, signatures that verify against the public key the channel would carry,
  the level `tpm`, the right PIN accepted, a wrong PIN refused (the TPM answers `NTE_PERM`), no signature of the
  approval key without the PIN or with a wrong one, the single-key set, and nothing left after the delete.
- **Software store** (the same program built with `BIATEC_KEYSTORE_WITHOUT_TPM`): the same, with the level
  `windows_account`, the lock after 5 wrong tries (also for the right PIN), acceptance after the wait, and no PIN
  record left after the delete.

That run found the fault that refused every enrollment before: "is this hardware" was asked of the key, a real TPM
answers "not supported" to that, and the key was taken for software. It is a property of the provider and is asked
there now.

Not covered by that program: the channel code (`key_channels.cpp`), Windows Hello, and the screens. The checklist
below is for those.

## Manual checklist (NOT run)

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
9. **No TPM.** In a virtual machine without a TPM: the sign-in and the enrollment complete, the key protection card
   is the warning "This computer is not the safest place for approvals" with the list of what to improve,
   `certutil -key` (the software provider) lists the keys, and `certutil -csp "Microsoft Platform Crypto Provider" -key`
   lists none. With Windows Hello set up, approvals ask for it; without, the app asks for a new PIN and for it at
   every unlock and approval; 5 wrong PINs give "refused for a while".

Without Windows Hello (the PIN of the app):

10. **A new PIN.** Sign in. After the browser the app shows "Choose a PIN for this app" and asks for a new PIN twice
    (Slovak texts show their diacritics). A PIN shorter
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
