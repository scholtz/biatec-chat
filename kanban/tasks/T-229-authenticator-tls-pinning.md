---
id: T-229
title: "Authenticator: TLS pinning for Biatec OIDC and a locked-down network config"
status: in-progress
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/security/pinning/**", "apps/biatec_authenticator/lib/approvals/approval_services.dart", "apps/biatec_authenticator/lib/enrollment/oidc_enrollment_client.dart", "apps/biatec_authenticator/android/app/src/main/res/xml/network_security_config.xml", "apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/ios/Runner/Info.plist", "apps/biatec_authenticator/test/pinning/pinning_test.dart", "apps/biatec_authenticator/docs/AUTHENTICATOR-PINNING.md"]
parallel: no
conflicts_with: [T-227]
issue: "#366"
branch: "task/T-229"
owner: "session-20261028-1135"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-229 - Authenticator: TLS pinning for Biatec OIDC and a locked-down network config

## Context

Spec: Security Standard rule 3. Today the app trusts any certificate the system trusts, including ones a user or a device administrator installed. A signing authenticator must talk only to Biatec OIDC.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

Calls to Biatec OIDC succeed only with a certificate whose public key matches a pin, with a backup pin and a documented rotation procedure.

## Scope

- In: SPKI SHA-256 pins per host (stage and production) and one backup pin, in configuration with a build define for the stage; an `http.Client` factory for the Dart side (`dart:io` `SecurityContext` with a bad-certificate callback that checks the leaf and intermediates against the pins, rejecting anything else, TLS 1.2 minimum); every OIDC client (approvals, enrollment, settings) uses it; Android `network_security_config.xml` with `cleartextTrafficPermitted=false`, no user CA trust in release, and `pin-set` with expiry; iOS ATS with no exceptions; a rotation document with how to add a new pin before the old one leaves; a clear error screen text for a pin failure that does not offer a bypass; the mock OIDC on localhost is allowed only in debug builds.
- Out: pinning the Firebase endpoints.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-227: they change the same files.

## Acceptance criteria

- [ ] A test server with a matching key is accepted; one with a valid chain but a different key is refused; an expired or self-signed certificate is refused; a plain `http://` URL in release is refused (existing rule stays).
- [ ] The pin set contains at least one backup pin, and a test fails when only one pin is configured for a production host.
- [ ] All three OIDC clients get their client from the factory (a source test fails when `http.Client()` is created elsewhere in `lib/`).
- [ ] The Android config has cleartext off and no `user` trust anchor in release; the debug config allows localhost only.
- [ ] `docs/AUTHENTICATOR-PINNING.md` explains where the pins come from, how to compute one, and the rotation steps.
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
- 2026-10-28 11:35 · session-20261028-1135 · backlog → in-progress · claimed
