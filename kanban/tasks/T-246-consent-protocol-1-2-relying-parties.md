---
id: T-246
title: "Consent protocol 1.2: relying parties - linking, payment, login and action requests, details hash (universal authenticator)"
status: done
priority: P2
epic: "Consent & authenticator"
blocked_by: [T-245]
area: ["packages/consent-protocol/spec/**", "packages/consent-protocol/schemas/**", "packages/consent-protocol/vectors/**", "packages/consent-protocol/scripts/**", "packages/consent-protocol/test/**", "packages/consent-protocol/mock-oidc/**", "packages/consent-protocol/conformance/**", "backend/Biatec.Chat.Tests/Canonical/**", "PRODUCT-DESCRIPTION.md", "docs/relying-party-protocol.md"]
parallel: no
conflicts_with: [T-243]
issue: "#406"
branch: "task/T-246"
owner: "session-20261115-0540"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-13
updated: 2026-11-15
---

# T-246 - Consent protocol 1.2: relying parties

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23, decisions D16 and D17 (revision 2). The server implementation is ludovit-scholtz/AlgorandGoogleDriveAccount#38 and copies the schemas and vectors this task produces; the app is T-247.

Owner decision (2026-11-13): Biatec Authenticator is to be a universal authenticator. Any organisation, a bank for example, can adopt it as its confirmation app for payments and for access to its internet banking. Biatec vouches for nobody: the organisation proves its domain, the user links it on the phone, and the organisation can verify every approval against the device key it saw at linking.

Protocol 1.1 (T-239) knows one request type, `biatec_transaction`, bound to an Algorand group id. 1.2 adds request types that are not transactions and the linking step.

## Goal

Spec, schemas, vectors, both reference verifiers and the mock describe protocol 1.2, so the server and the app can be built against them independently.

## Scope

- In:
  - **Spec chapter "Relying parties"**: the trust model (four statements of issue 38), registration (`private_key_jwt`, CIBA grant only, required `client_uri`, the well-known file `/.well-known/biatec-client.json`), linking, direct requests, receipt verification with a pinned device key, what a new device means for a pinned key.
  - **Text rules** (one normative section, referenced by every string a relying party supplies and by the user's label): Unicode NFC; no control characters; no bidirectional controls (U+202A to U+202E, U+2066 to U+2069, U+200E, U+200F, U+061C); no zero-width characters (U+200B to U+200D, U+2060, U+FEFF); no line breaks; the length limits are in code points. A violation is refused, never stripped. Provide `isDisplaySafe(text)` in `scripts/lib.mjs` and vectors `vectors/text-rules.json` (accepted and refused strings, each with the reason).
  - **Schemas** (`schemas/`), all `additionalProperties: false`:
    - `authorization-details-payment.schema.json`: `{ type: "biatec_payment", instructed_amount: { amount (decimal string, at most 2 decimals, greater than 0), currency (3 upper-case letters) }, creditor_name (1 to 70), creditor_account: { iban } | { identification (1 to 34), scheme (1 to 20) }, remittance? (at most 140), execution_date? (date), origin? }`;
    - `authorization-details-login.schema.json`: `{ type: "biatec_login", context?: { ip?, location?, user_agent? } (each at most 80), origin? }`;
    - `authorization-details-action.schema.json`: `{ type: "biatec_action", title (1 to 60), lines: [ { label (1 to 30), value (1 to 80) } ] (1 to 8), origin? }`;
    - `link-request.schema.json` (what the device fetches) and `link-signed.schema.json`: `{ type: "biatec_link", link_id, client_fingerprint, domain (string or null), decision: "approve" | "deny", binding_code, nonce, issued_at, kid }`;
    - the consent request and receipt schemas accept the three new types, and the receipt's signed object carries `details_hash` for them (`group_hash` stays for `biatec_transaction`; exactly one of the two is present).
  - **`details_hash`** = base64url without padding of SHA-256 over RFC 8785 of the `authorization_details` object exactly as the relying party sent it plus the server's `origin` when present. The device computes it itself from the object it renders and never copies it from the request. Worked example in the spec.
  - **Verification** (section 8): steps for the new types; an `approve` for any of them needs the approval key (`biatec_key_use: "approve"`); verification with a pinned device JWK and no attestation is a defined mode (`verifyReceiptPinned(receipt, deviceJwk, expectedDetails)` in `lib.mjs`).
  - **Link code in the QR**: `biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>`; the app refuses an `iss` other than the server it is enrolled with.
  - **Vectors**: `vectors/receipts-1.2.json` (approve and deny for each type; an IBAN altered by one character fails the hash; a payment approval signed by the presence key that verifiers must refuse) and `vectors/link.json` (approve, deny, wrong domain). Extend the generator script; do not hand-edit signatures.
  - **Reference verifiers**: `scripts/lib.mjs` and `backend/Biatec.Chat.Tests/Canonical/ConsentProtocolVectorTests.cs` pass every new vector.
  - **Mock** (`mock-oidc`): registration of a `private_key_jwt` client (assertion checks: `iss` = `sub` = `client_id`, audience, expiry, `jti` single use, ES256 or PS256 only); `POST /consent/links`, the device-side `GET` and `POST …/receipt`, the client-side `GET`, `DELETE /consent/links/subject/{subject}`, `GET /consent/links/subject/{subject}/devices`; `/connect/ciba` with `authorization_details` of the three types (always to the phone; unlinked subject `400 unknown_user_id`; text-rule violations `400 invalid_authorization_details`); the redeemed token carries scope `openid` only and is refused everywhere. Shapes exactly as issue 38.
  - **Conformance kit**: cases for registration, linking, each request type, pinned verification, and every refusal above.
  - **Sample relying party** `packages/consent-protocol/scripts/rp-demo.mjs`: registers against a given server, prints the QR payload and the code, waits for the link, sends a payment request, polls, fetches and verifies the receipt with the pinned key, printing each step. Used for the manual test of T-247. Keys are generated in memory; nothing is written to disk.
  - **`PRODUCT-DESCRIPTION.md`**: a section "Universal authenticator" under the authenticator chapter stating the decision, the trust model and the three request types; `docs/relying-party-protocol.md` pointing to the spec chapter.
- Out: Biatec OIDC (issue 38); the app's screens (T-247); real bank formats beyond the fields above (ISO 20022 mapping is the relying party's job).

## Parallel work

Parallel work possible: **no**: T-243 changes the same conformance kit, so do not run at the same time as it (and only after T-245, which also changes the mock).

## Acceptance criteria

- [x] `pnpm --filter consent-protocol test` passes with the new vectors, including "altering any one signed member invalidates the signature" for `biatec_link` and for each new receipt type, and the text-rule vectors.
- [x] `pnpm --filter mock-oidc test` and `pnpm --filter conformance test` pass, including: a client that was not linked cannot open a request; a `private_key_jwt` client cannot obtain `sign`; an assertion replay is refused.
- [x] `dotnet test backend --filter ConsentProtocolVectorTests` passes.
- [x] `node packages/consent-protocol/scripts/rp-demo.mjs` runs to the end against the mock with the mock's test device approving (scripted), and verifies the receipt with the pinned key only.
- [x] The spec has a "Changes from 1.1" section. Envelope `version` is `"1.2"` for the new types and stays `"1.1"` for `biatec_transaction`.

## Implementation notes

- Validate IBANs by the mod-97 checksum in the mock and in `lib.mjs`.
- Amounts are decimal strings; never floats.
- A relying party's strings are untrusted data on a security screen: when in doubt, refuse.

## Verification

`pnpm --filter consent-protocol test && pnpm --filter mock-oidc test && pnpm --filter conformance test && dotnet test backend --filter ConsentProtocolVectorTests`

## Hand-off

PR #417, `task/T-246` @ 1916db2. Local: protocol tests 302 pass, `dotnet test backend --filter ConsentProtocolVectorTests` 19 pass, mock-oidc 116, conformance 107, e2e 10, `scripts/rp-demo.mjs` runs to the end against the mock and verifies the receipt with the pinned key only. CI: backend, js, images, authenticator-security pass; `authenticator` fails only on the Flutter SDK drift that also fails on main (this PR touches no authenticator file).

- Spec chapters 14 (Relying parties), 15 (text rules: relying-party profile, label profile, shared core), 16 (Changes from 1.1); `details_hash` with a worked example; version "1.2" for the new types, "1.1" for `biatec_transaction`. Schemas `authorization-details-payment|login|action`, `link-request`, `link-created`, `link-status`, `link-signed`; vectors `receipts-1.2.json`, `link.json`, `text-rules.json` (generated); `lib.mjs` (`detailsHash`, `isValidIban`, `verifyReceiptPinned`, `verifyLink`, `verifyLinkPinned`); C# vector tests; `PRODUCT-DESCRIPTION.md` "Universal Authenticator" and `docs/relying-party-protocol.md`.
- Mock: `private_key_jwt` registration, links, CIBA with the three request types, `openid`-only token refused everywhere; 40+ conformance cases; `rp-demo.mjs`.
- Honest trust limit (stated in the spec and the product description): at link time the party trusts the operator for WHICH device key it pins (the key list comes from the same answer); after pinning, the operator cannot alter or forge a receipt for that key.
- Open points for Biatec OIDC issue 38 (nothing here decides them for the server): bodyless party calls use `Authorization: Client-Assertion <jwt>` in the mock; the spec does not say what the app puts in `device.attestation` of a 1.2 receipt (the mock mints it for the pairwise subject when the receipt is stored: T-247 and the server must agree); `X-Link-Secret` is required on the device receipt call in the mock; the receipt endpoint is served at both `/consent/receipts/{id}` and the issue-38 path; an unlinked pending request ends as `access_denied` with reason `unlinked`.
- Not modelled: a hardened domain fetcher and the daily re-check, `service_linked` event/email; the conformance `.ts` files cannot be typechecked (no tsc in that package).

**Unblocks:** T-247 (link by QR, payment and login cards).

## Review

- 2026-11-15 02:06 · approve · PR #417 merged into main @ f8ee42f (recorded by kanban-sync)

## Activity log

- 2026-11-13 09:05 · scholtz · (new) → blocked · created from revision 2 of the consent architecture record (AlgorandGoogleDriveAccount issue 23): universal authenticator
- 2026-11-15 00:42 · kanban-board · blocked → backlog · blockers done: T-245
- 2026-11-15 05:40 · scholtz · backlog → in-progress · claimed by session-20261115-0540 on task/T-246
- 2026-11-15 12:00 · scholtz · in-progress → review · PR #417 @ 1916db2
- 2026-11-15 02:06 · user · review → done · PR #417 merged into main @ f8ee42f (synced by kanban-sync)
