---
id: T-230
title: "Authenticator: security documentation, MASVS matrix and CI checks"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-222, T-223, T-227, T-228, T-229]
area: ["docs/AUTHENTICATOR-SECURITY.md", "docs/threat-model.md", ".github/workflows/ci.yml", "apps/biatec_authenticator/tool/**", "docs/architecture.md"]
parallel: yes
conflicts_with: []
issue: "#367"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-230 - Authenticator: security documentation, MASVS matrix and CI checks

## Context

Spec: Security Standard rule 1 asks for a conformance matrix against OWASP MASVS v2.0 Level 2 and an independent penetration test before release. This task writes the matrix from what the other tasks built, and adds the CI checks that keep it true.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

`docs/AUTHENTICATOR-SECURITY.md` maps every MASVS L2 control to the code, test or document that meets it (or says plainly that it does not), and CI fails when the app regresses on the checks that can be automated.

## Scope

- In: the matrix (control id, status met, partly, not applicable or open, evidence link); the threat model section for the authenticator (assets, attackers, abuse cases, mitigations) added to `docs/threat-model.md`; a pen-test brief (scope, build, test accounts, what to try, how to report); CI jobs: dependency vulnerability scan of `pubspec.lock` with OSV, a check that the lockfile is committed and unchanged by `pub get`, an SBOM (CycloneDX) artifact, the platform-hardening and pinning tests, a grep gate for `print(` and `debugPrint(` in `lib/` and for secrets-like strings, and a signed-release checklist; update `docs/architecture.md` where it says what the authenticator does.
- Out: the penetration test itself, and new product features.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Every MASVS v2.0 L2 control appears in the matrix with a status and evidence; 'met' rows link to a file or a test that exists (a script checks the links).
- [ ] CI runs the OSV scan, lockfile check, SBOM, and the grep gate, and fails on a seeded violation (shown in the PR).
- [ ] The threat model names at least: stolen unlocked phone, stolen locked phone, malware with accessibility, overlay and screen-capture attacks, rooted device, network attacker with a trusted CA, a malicious QR code, a poisoned backup file, a phishing request that looks like a bank.
- [ ] The pen-test brief exists and lists the build and the checklist.
- [ ] Open items are listed with an owner or a task id, not hidden.

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
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-222, T-223, T-227, T-228, T-229
- 2026-10-28 09:10 · scholtz · taken · status stays blocked
