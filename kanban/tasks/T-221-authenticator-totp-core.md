---
id: T-221
title: "Authenticator: two-factor code engine (HOTP, TOTP, otpauth)"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/totp/core/**", "apps/biatec_authenticator/test/totp/core/**"]
parallel: yes
conflicts_with: []
issue: "#358"
branch: "task/T-221"
owner: "session-20261028-0915"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-221 - Authenticator: two-factor code engine (HOTP, TOTP, otpauth)

## Context

The spec adds general-purpose two-factor codes to the authenticator ("Two-Factor Codes" in PRODUCT-DESCRIPTION.md). This task is the pure-Dart engine; storage, UI and the lock come in later tasks.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

A tested library that parses `otpauth://` links and Base32 keys and produces HOTP and TOTP codes exactly as the RFCs and common services do.

## Scope

- In: `OtpAccount` model (issuer, label, secret bytes, algorithm SHA-1, SHA-256, SHA-512, digits 6 to 8, period 15 to 120 s, type totp or hotp, counter); Base32 decoding (RFC 4648, spaces, dashes, case and missing padding tolerated); `otpauth://totp|hotp` parser and writer (issuer from path and from the `issuer` parameter, percent-decoding, validation with specific errors); HOTP (RFC 4226) and TOTP (RFC 6238) generators with an injectable clock; digit grouping for display; time left in the period; secrets held as `Uint8List` with a `wipe()`; no logging of secrets, and `toString` that never prints one.
- Out: storage (T-223), UI (T-224), QR scanning (T-224).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] HOTP matches the RFC 4226 Appendix D test values; TOTP matches the RFC 6238 Appendix B values for SHA-1, SHA-256 and SHA-512.
- [ ] The parser accepts the forms real services emit (Google, GitHub, Microsoft, Biatec) and rejects: wrong scheme or type, missing secret, invalid Base32, digits outside 6 to 8, period outside 15 to 120, unknown algorithm, a counter for TOTP or a missing counter for HOTP, each with a distinct error.
- [ ] Writing an account and parsing it again gives the same account (round trip), including issuer and label with special characters.
- [ ] `OtpAccount.toString()` and any exception message never contain the secret.
- [ ] Codes are correct across a period boundary and at the Unix epoch edge cases, with the clock injected.
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

- 2026-10-28 09:00 · claude · (new) → backlog · created from the request to add two-factor codes, a biometric lock and industry-standard security
- 2026-10-28 09:10 · scholtz · taken · status stays backlog
- 2026-10-28 09:15 · session-20261028-0915 · backlog → in-progress · claimed
