---
id: T-245
title: "Authenticator: the user's own threshold, linked services, trusted applications (all trust is set on the phone)"
status: done
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-242]
area: ["apps/biatec_authenticator/lib/settings/**", "apps/biatec_authenticator/lib/clients/**", "apps/biatec_authenticator/lib/approvals/**", "apps/biatec_authenticator/test/settings/**", "apps/biatec_authenticator/test/clients/**", "apps/biatec_authenticator/test/approvals/**", "packages/consent-protocol/mock-oidc/**", "packages/consent-protocol/conformance/**"]
parallel: no
conflicts_with: [T-243]
issue: "#405"
branch: "task/T-245"
owner: "session-20261115-0215"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-13
updated: 2026-11-15
---

# T-245 - Authenticator: threshold, linked services, trusted applications

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decisions D3, D11, D13 and D16 (revision 2). Server side: ludovit-scholtz/AlgorandGoogleDriveAccount#27 (settings and policy) and #30 (changing settings). Signed shapes: T-239.

Owner decisions (2026-11-13): the operator trusts no client and no application id; every trust decision is the user's, made on the phone. The 1000 EUR threshold is only the starting value; the user sets their own.

## Goal

A person can see and change, on their phone, when they are asked, which services are linked to their authenticator and which applications each service may call without asking; and nothing on any screen is a name the operator or the service asserted without proof.

## Scope

- In:
  - **How a service is shown, everywhere** (one widget, `lib/clients/service_identity.dart`): the proven domain in its ASCII form, or "Unproven application" in the warning style with the fingerprint `xxxx-xxxx`; under it the user's own label when set ("You call it: …"). Never a name, logo or badge from the server. Delete any rendering of `verified`, `entity_id` or a client display name.
  - **Approval card** (`approval_card.dart`):
    - a request of a service that is not linked shows a "New service" banner: "Approving links this service to your authenticator. It can then send you requests. You can unlink it in Settings.";
    - for each application of the request with `trusted: false`, a checkbox, unticked: "Don't ask again for application <id> from <domain or fingerprint> while the amount is under my threshold". Ticked ids go into the receipt's signed `remember.applications` (T-239), so the one biometric prompt covers them. Offered for every untrusted application of the request; Deny never carries `remember`.
  - **Settings → Security → Approval threshold**: an amount field in EUR with the current value, the choices "Always ask" (0), 100, 500, 1000, 5000 and a free value (0 to the server's maximum; the server's `400` text is shown when an operator maximum exists). Lowering applies at once. Raising explains "Requests under this amount will be signed without asking you" and is signed by the approval key (`biatec_settings_change`; one biometric prompt). Also here: "Ask on test networks" (turning it off is loosening) and "Block services without a proven domain".
  - **Settings → Security → Linked services** (`clients_screen.dart`): each linked service with its identity widget, the date linked, and a detail screen: your label (edit; 40 characters; the same text rules as the server), "Always ask for this service", its own threshold (optional), trusted applications (network and id, each removable), pauses (lift is loosening), and **Unlink** (confirm; tightening; the next request from it is "New service" again).
  - **Loosening vs tightening** is decided by the server's answer: send the change; on `403 insufficient_user_authentication` with `reason: consent_required`, sign it with the approval key and resend. Do not duplicate the classifier in the app.
  - Refresh on the `settings_changed` event (T-244) and on screen open; an inactive device sees everything read-only with the reason.
  - **Mock** (`mock-oidc`): the settings members of issue 30 (`threshold_eur` without a ceiling unless `MOCK_MAX_THRESHOLD_EUR` is set, `ask_on_test_networks`, `block_unproven_clients`, per client `label`, `trusted_applications_add` / `_remove`, `unlink`), linking on the first approved request, `remember` on the receipt (refused when an id is not in the request), `first_use` true until linked, and the policy using the user's threshold and trusted applications. Conformance cases for each.
  - English and Slovak strings.
- Out: linking by QR code and payment or login cards (T-247); the server (issues 27 and 30).

## Parallel work

Parallel work possible: **no**: T-243 changes the same conformance kit, so do not run at the same time as it (and only after T-242; both change the mock and the app's screens, so they are ordered).

## Acceptance criteria

- [x] Widget tests: proven domain, unproven application, user label; no test fixture can make a server-supplied name appear (a fixture with `display_name` and `verified` members renders neither).
- [x] Card: "New service" banner only when not linked; ticking an application puts exactly that id in `remember.applications` of the signed receipt and the receipt verifies with `packages/consent-protocol/scripts/lib.mjs`; Deny never carries `remember`.
- [x] Threshold: lowering sends no signature; raising signs with the approval key exactly once; 0 shows "Always ask"; a value above an operator maximum shows the server's refusal.
- [x] Linked services: label edit, per-service threshold, removing a trusted application, unlink, lifting a pause (signed).
- [x] Contract tests against the mock: after approving with `remember`, the same application under the threshold is signed without a request; after unlink the next request is first use; with the threshold raised to 5000 a 2000 EUR transfer is signed without a request.
- [x] `flutter analyze`, `flutter test`, `pnpm --filter mock-oidc test` and `pnpm --filter conformance test` pass.

## Implementation notes

- Amounts are decimal strings end to end; never parse to a double.
- A label is the user's text, but it is still rendered next to a security decision: apply the same text rules as the server before sending, and render it in a different style from the domain.
## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test && cd ../.. && pnpm --filter mock-oidc test && pnpm --filter conformance test`

## Hand-off

PR #416, `task/T-245` @ 05abcda. Local: `flutter test` 1346 pass, mock-oidc 78, conformance 63, e2e 10, the settings and approval contract tests pass against a mock started with `MOCK_MAX_THRESHOLD_EUR=5000`. CI: backend, js, images, authenticator-security pass; `authenticator` fails only on the Flutter SDK drift that also fails on main (it stops at `flutter analyze`, so the Dart tests only ran locally).

- One identity widget (proven ASCII domain or "Unproven application" with the fingerprint, plus the user's own label); no server name, logo or `verified` anywhere. Approval card: "New service" banner, unticked "don't ask again" per untrusted application into the signed `remember.applications`, Deny never carries it, the receipt verifies with `scripts/lib.mjs` (that test shells out to `node` and skips when node is missing, as it is in the CI authenticator job).
- Threshold screen (decimal strings, Always ask / 100 / 500 / 1000 / 5000 / free, the server's 400 text, switches with confirmation for loosening) and Linked services (label, always ask, own threshold, trusted applications, pauses, unlink). The server decides loosening; the app signs with the approval key only on `403 consent_required`.
- Gated by `TRUST_SETTINGS` (on for the mock, off against real Biatec OIDC; the legacy settings screens keep the old keys that real OIDC understands).
- Mock and conformance: issue 30 settings, linking on first approval, `remember`, unlink resets first use, 11 conformance cases; the mock README section "Settings (issue 30, T-245)" is the interface source of truth.
- Deviation from the task text: the spec (secure-area rule 4) wins, so a fresh biometric (the app gate) precedes EVERY settings change; a loosening therefore costs the gate prompt plus the approval-key prompt (the signature is still made once), where the task said "exactly once".
- NOT verified on a device (biometric prompts, Android/iOS screens). The operator ceiling is covered by a mock test only, not by a conformance case. Fingerprint shown is the four-group form `xxxx-xxxx-xxxx-xxxx`. The label NFC check in Dart is approximate (no normaliser): the server still checks NFC.

**Unblocks:** T-246 (consent protocol 1.2 for relying parties).

## Review

- 2026-11-15 00:42 · approve · PR #416 merged into main @ 2071fc0 (recorded by kanban-sync)

## Activity log

- 2026-11-13 09:05 · scholtz · (new) → blocked · created from revision 2 of the consent architecture record (AlgorandGoogleDriveAccount issue 23): all trust is the user's, threshold is the user's
- 2026-11-14 23:55 · kanban-board · blocked → backlog · blockers done: T-242
- 2026-11-15 02:15 · scholtz · backlog → in-progress · claimed by session-20261115-0215 on task/T-245
- 2026-11-15 05:30 · scholtz · in-progress → review · PR #416 @ 05abcda
- 2026-11-15 00:42 · user · review → done · PR #416 merged into main @ 2071fc0 (synced by kanban-sync)
