---
id: T-228
title: "Authenticator: device integrity checks that fail closed"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-222, T-227]
area: ["apps/biatec_authenticator/lib/integrity/**", "apps/biatec_authenticator/test/integrity/**", "apps/biatec_authenticator/android/app/src/main/**/Integrity*", "apps/biatec_authenticator/ios/Runner/Integrity*", "apps/biatec_authenticator/lib/lock/**"]
parallel: yes
conflicts_with: []
issue: "#365"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-28
updated: 2026-10-28
---

# T-228 - Authenticator: device integrity checks that fail closed

## Context

Spec: Security Standard rule 5. The authenticator guards the user's authentication secrets, so a rooted or jailbroken device, an attached debugger or a hooking framework is treated as hostile. The attestation by Play Integrity and App Attest at enrollment is in the spec and in the OIDC required changes; this task is the local side and the policy.

Read first: `PRODUCT-DESCRIPTION.md` (Biatec Authenticator, Authenticator App (Flutter) and its subsections), `AGENTS.md`, `apps/biatec_authenticator/README.md`. Security rules in the spec are requirements, not suggestions: when in doubt choose the stricter reading and say so in the PR.

## Goal

On a compromised device the release app refuses to open the secure area and says why; on a healthy device nothing changes.

## Scope

- In: an `IntegrityChecker` interface with platform implementations (Android: root indicators such as su binaries, Magisk and test-keys, writable system paths, debugger attached, `TracerPid`, Frida server and gadget traces, hooking framework classes, emulator properties, a signature check against the expected release certificate digest; iOS: jailbreak files and writable paths, debugger via `sysctl`, injected dylibs, simulator); a result with a list of reasons that is shown to the user but carries no detail an attacker could use to tune a bypass; a policy in `LockController` (T-222): release builds fail closed, debug builds warn; the check runs at launch, on resume and before each approval; a hook for Play Integrity and App Attest tokens at enrollment (the call is built; the server side is in the OIDC required changes).
- Out: server-side verification of attestation (Biatec OIDC).

## Parallel work

Ordering: T-227 change files this task also edits (MainActivity, the manifests, Info.plist or pubspec.yaml), so they go first.

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Each detector is unit tested against fixtures (a fake file system and fake process info) for compromised and healthy cases.
- [ ] A compromised result in release mode keeps the secure area locked and shows the reason screen; in debug mode it shows a warning only (tests for both modes with an injected build mode).
- [ ] The result is cached for a short time and refreshed on resume and before each approval.
- [ ] The reason screen does not name the detector that fired.
- [ ] The expected release signing digest comes from configuration, and a mismatch is a compromised result.
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
- 2026-10-28 09:42 · kanban-board · backlog → blocked · waiting on: T-222
- 2026-10-28 09:10 · scholtz · taken · status stays blocked
