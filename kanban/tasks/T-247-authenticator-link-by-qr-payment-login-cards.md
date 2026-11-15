---
id: T-247
title: "Authenticator: link a service by QR code, and approval cards for payments, logins and actions"
status: review
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-246]
area: ["apps/biatec_authenticator/lib/links/**", "apps/biatec_authenticator/lib/approvals/**", "apps/biatec_authenticator/lib/clients/**", "apps/biatec_authenticator/android/app/src/main/AndroidManifest.xml", "apps/biatec_authenticator/ios/Runner/Info.plist", "apps/biatec_authenticator/test/links/**", "apps/biatec_authenticator/test/approvals/**", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#407"
branch: "task/T-247"
owner: "session-20261115-1210"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-11-13
updated: 2026-11-15
---

# T-247 - Authenticator: link by QR, payment, login and action cards

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decisions D16 and D17. Protocol: T-246 (spec chapter "Relying parties", schemas, vectors, mock). Server: ludovit-scholtz/AlgorandGoogleDriveAccount#38.

With this task a bank's customer can add the bank to their Biatec Authenticator and confirm the bank's payments and logins there.

## Goal

A person scans a service's QR code, sees who it is by proven domain, links it with a code and a biometric, and afterwards approves or denies that service's payment, login and action requests on cards that show exactly what is signed.

## Scope

- In:
  - **Link entry.** "Link a service" in Settings → Linked services opens the scanner (`mobile_scanner`, already a dependency); the app also handles the `biatec-authenticator://link` URI when opened from another app. Parse strictly: scheme, host `link`, exactly the parameters `iss`, `id`, `s`; refuse anything else and an `iss` that is not the enrolled server (compare origins exactly; show "This code is for a different authenticator service").
  - **Link screen.** Fetch the link (`GET /consent/links/{id}` with `X-Link-Secret`). Show the service identity widget (T-245), "The service calls you: <user_label>" in the "written by the service" style when present, and what linking means ("This service will be able to send you approval requests. It cannot move anything without your approval."). The user types the 6 digit code shown by the service; **Link** signs `biatec_link` with the approval key (one biometric prompt), **Don't link** signs a deny with the presence key. A service without a proven domain needs an extra confirmation ("This service has not proven who it is"). Wrong code: the server's remaining attempts are shown; expired or used link: a neutral message.
  - **Cards** (`lib/approvals/cards/`), one per type, chosen by `authorization_details.type`; an unknown type or a request that fails its schema is shown as "This request cannot be shown; update the app" and cannot be approved:
    - `biatec_payment`: amount and currency in the largest type, creditor name, creditor account (IBAN grouped in fours; other identifiers with their scheme), remittance and execution date when present;
    - `biatec_login`: "Sign in to <domain or fingerprint>", and the context lines under the heading "Reported by the service" in the secondary style;
    - `biatec_action`: the title and the labelled lines inside a bordered area headed "Written by the service".
    Every card shows the service identity widget, the origin when present, the expiry countdown and the binding-code entry, like the transaction card.
  - **Display safety.** Before rendering, every service-supplied string is checked with the text rules of T-246 (a Dart port of `isDisplaySafe`, tested against `vectors/text-rules.json`); a violation makes the request not approvable. Strings are rendered as plain text, single line or wrapped, never as markup or links.
  - **What is signed.** The app computes `details_hash` itself from the `authorization_details` object it rendered (RFC 8785 with the existing `jcs.dart`, SHA-256, base64url) and puts it in the receipt. It never copies a hash from the request, and it refuses to approve when the request's own `details_hash` differs from the computed one.
  - **History** entries for links and for the new request types (type, service, amount for payments, decision, time).
  - English and Slovak strings.
- Out: protocol and mock (T-246); the server (issue 38).

## Parallel work

Parallel work possible: **yes** (after T-246).

## Acceptance criteria

- [x] Parser tests: a valid link; extra parameter, missing parameter, other scheme, other `iss` → refused with the right message.
- [x] Link flow against the mock: approve creates the link and the service appears in Linked services; deny does not; three wrong codes close the link; the approve signature is by the approval key and verifies with `lib.mjs`.
- [x] Card tests for each type from the T-246 vectors; an unknown type and a string with a right-to-left override are not approvable.
- [x] `details_hash`: the app's value equals the vector's for every 1.2 receipt vector; a request whose stated hash differs is refused.
- [ ] `flutter analyze` and `flutter test` pass.
- [ ] Manual checklist in the README using `packages/consent-protocol/scripts/rp-demo.mjs` against the mock on a real phone: scan, link, approve a payment, deny a login. The Hand-off says whether it was run.

## Implementation notes

- The deep link is an input from another app: treat it exactly like a scanned code, and never act on it without the screen above.
- Do not auto-submit the binding code; the user confirms.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`

## Hand-off

PR #418, `task/T-247` @ dde96b8. Local: `flutter test` 1563 pass; the link, approval and settings contract tests pass against the mock (started and killed in one command; not re-run after the last round). CI: backend, js, images, authenticator-security pass; `authenticator` fails only on the Flutter SDK drift that also fails on main (it stops at `flutter analyze`, so the Dart tests ran locally only).

- Link entry from Settings → Linked services (QR scanner or pasted code) and the `biatec-authenticator://link` deep link, parsed strictly; a wrong `iss` is refused before anything is sent; a deep link is handled exactly like a scanned code. Link screen with the service identity, the service's own label in a "Written by the service" area, a typed six-digit code (never auto-submitted), Link signed with the approval key, Don't link with the presence key, an extra confirmation for an unproven domain, remaining attempts, a "check again" path after a lost answer.
- Cards for payment, login and action; an unknown type or a schema/text-rule failure is "This request cannot be shown; update the app" and cannot be approved (a local, in-memory Dismiss clears it for this run). A Dart port of the relying-party text profile passes all 60 `text-rules.json` vectors. The app computes `details_hash` itself and refuses a request whose own hash differs. Service-written text never appears in the app's own style: the list card and the OS prompt use fixed app words plus the service identity and, for payments, the amount.
- Gated by configuration against real Biatec OIDC: `RELYING_PARTIES` (needs the two keys and the trust settings too; on for the mock).
- NOT verified on a device: camera scan, deep link from another app, biometric prompts, iOS URL scheme delivery, the manual checklist (`scripts/rp-demo.mjs --manual`, in the README). No device criterion is ticked. How Flutter 3.50 hands a deep link to Dart is confirmed only by a platform-message test.
- Decisions and limits: the mock's issuer is always `https://oidc.biatec.test`, so the app accepts that exact issuer only for a build against the mock or a plain `http` server (an `https` build has no exception); the Dart text-rule port refuses a combining mark after a Latin/Greek/Cyrillic letter (no normaliser; it can refuse text the server accepts, never the reverse); the custom URL scheme is claimable by other apps (the link secret is single-use and lives 5 minutes); a 1.2 receipt carries the device's own attestation (the spec does not say otherwise; the server must agree); dismissed requests come back after a restart; `deny` on a link sends an empty binding code.
- Files outside the task's area list: `lib/config.dart`, `lib/main.dart`, `lib/security/settings_controller.dart`, `lib/shell/app_shell.dart` (flag, deep-link routing, intake), and a small refactor into `lib/approvals/device_identity.dart` and `lib/text/`.

**Unblocks:** nothing (last task of the consent epic).

## Review

## Activity log

- 2026-11-13 09:05 · scholtz · (new) → blocked · created from revision 2 of the consent architecture record (AlgorandGoogleDriveAccount issue 23): universal authenticator
- 2026-11-15 02:06 · kanban-board · blocked → backlog · blockers done: T-246
- 2026-11-15 12:10 · scholtz · backlog → in-progress · claimed by session-20261115-1210 on task/T-247
- 2026-11-15 15:30 · scholtz · in-progress → review · PR #418 @ dde96b8
