---
id: T-223
title: "Authenticator: hardware-bound encrypted vault for two-factor accounts"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-221, T-227]
area: ["apps/biatec_authenticator/lib/totp/vault/**", "apps/biatec_authenticator/test/totp/vault/**", "apps/biatec_authenticator/android/app/src/main/**/Vault*", "apps/biatec_authenticator/ios/Runner/Vault*"]
parallel: yes
conflicts_with: []
issue: "#360"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-223 - Authenticator: hardware-bound encrypted vault for two-factor accounts

## Context

Spec: Two-Factor Codes rules 5 and 6, and Secure Area rule 3. Seeds are the most valuable thing on the phone. The existing `PlatformDeviceKeyStore` shows how the app talks to the Secure Enclave and StrongBox through platform channels; read it and `lib/keys/**` first.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

Seeds are stored encrypted under a vault key that lives in hardware, needs the user's biometric to use, is invalidated when biometrics change, and never appears in a backup.

## Scope

- In: `VaultKeyStore` platform channel (Android Keystore AES-256 key, StrongBox when available, `setUserAuthenticationRequired(true)`, strong biometric only, invalidated on new enrollment; iOS Secure Enclave-protected key with `biometryCurrentSet` access control, `ThisDeviceOnly`); AES-256-GCM envelope encryption in Dart or native with a fresh 96-bit nonce per encryption and the account id as associated data; a `TotpVault` (add, list metadata without secrets, read secret for one account after a biometric, update, delete, reorder, counter increment for HOTP) over an encrypted file in app-private storage; atomic writes; a corruption and rollback check (versioned, authenticated index); the `biometricsChanged` result when the key is invalidated; a fake in-memory key store for tests; the vault excluded from backups.
- Out: the lock gate (T-222), the UI (T-224), export and import (T-226).

## Parallel work

Ordering: T-227 change files this task also edits (MainActivity, the manifests, Info.plist or pubspec.yaml), so they go first.

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Round trip: an account added and read back is identical; secrets and plaintext never appear in the file (a test searches the file bytes for the secret and the issuer).
- [ ] Tampering with the file, swapping two accounts' ciphertexts, truncating it, or replacing it with an older version is detected and reported, never decrypted silently.
- [ ] Nonces are never reused (a test encrypts many times and checks uniqueness); the associated data binds ciphertext to its account id.
- [ ] A key invalidated by a biometric change makes every read return `biometricsChanged` and leaves the file intact for a later restore.
- [ ] Metadata listing (issuer and label) needs no biometric; reading a secret does, and the platform key is created with the biometric-bound flags (a native test or a code-level assertion per platform).
- [ ] Secrets are wiped from memory after use in the vault API.
- [ ] `flutter analyze` is clean and `flutter test` passes in `apps/biatec_authenticator`, with new tests for every behaviour above (the failing case first for each security rule).

## Implementation notes

Write the failing test first for every security rule. Never log, print or put in an exception message any secret, code, token or key. Use platform cryptography, not your own. Keep widget keys that existing tests use, or update those tests in the same change.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`; for native work also build the platform that changed (`flutter build windows` proves only the Dart side; say in the hand-off what could not be run on a device).

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-28 09:00 · claude · (new) → blocked · created from the request to add two-factor codes, a biometric lock and industry-standard security
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-221
- 2026-10-28 09:10 · scholtz · taken · status stays blocked
