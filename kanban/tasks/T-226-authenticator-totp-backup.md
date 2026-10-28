---
id: T-226
title: "Authenticator: encrypted backup and restore of two-factor accounts"
status: blocked
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-223, T-224]
area: ["apps/biatec_authenticator/lib/totp/backup/**", "apps/biatec_authenticator/lib/codes/backup_screen.dart", "apps/biatec_authenticator/test/totp/backup/**", "apps/biatec_authenticator/pubspec.yaml"]
parallel: yes
conflicts_with: []
issue: "#363"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-226 - Authenticator: encrypted backup and restore of two-factor accounts

## Context

Spec: Two-Factor Codes rule 7. Without a backup, a biometric change or a lost phone loses every seed; with a plain export, a stolen file loses them all. The backup is the user's own file, encrypted with a passphrase.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

The user can export an encrypted backup after a fresh biometric and restore it on this or another phone, and nothing else can read it.

## Scope

- In: file format (versioned header, random 16-byte salt, Argon2id with m=64 MiB, t=3, p=4 as the floor and the parameters stored in the header, AES-256-GCM with a random nonce, the header as associated data); passphrase rules (length of at least 12 characters, a strength estimate, refuse the weakest, confirmation field); export through the system share sheet with the file name carrying no account names; import that shows what will be added and handles duplicates; restore from the 'biometrics changed' screen; wrong passphrase and tampered file give the same generic failure; Argon2id from a maintained package (for example `pointycastle`, already a dev dependency) with a test against RFC 9106 vectors.
- Out: cloud sync (never).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Export then import reproduces every account exactly, on a fresh vault.
- [ ] A wrong passphrase, a flipped bit anywhere, a truncated file and a downgraded parameter set all fail without revealing which; Argon2id parameters below the floor in a file are refused.
- [ ] The file contains no plaintext issuer, label or secret (a byte search test).
- [ ] Export and import require a fresh biometric; the passphrase is never stored or logged.
- [ ] The export never uses a weaker KDF than the floor even on slow devices, and shows progress while it runs off the UI thread.
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
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-223, T-224
- 2026-10-28 09:10 · scholtz · taken · status stays blocked
