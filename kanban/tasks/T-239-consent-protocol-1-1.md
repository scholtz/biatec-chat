---
id: T-239
title: "Consent protocol 1.1: whole-group details, signed origin, two device keys, device-signed settings and device approval, user-held trust"
status: review
priority: P0
epic: "Consent & authenticator"
blocked_by: []
area: ["packages/consent-protocol/spec/**", "packages/consent-protocol/schemas/**", "packages/consent-protocol/vectors/**", "packages/consent-protocol/scripts/**", "packages/consent-protocol/test/**", "backend/Biatec.Chat.Tests/Canonical/**", "PRODUCT-DESCRIPTION.md"]
parallel: yes
conflicts_with: []
issue: "#399"
branch: "task/T-239"
owner: "session-20261113-1511"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-12
updated: 2026-11-13
---

# T-239 - Consent protocol 1.1

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23. It is the first step of phase 1: the Biatec OIDC issues 26 (group description), 32 (two keys), 30 (settings) and 31 (enrollment) copy the schemas and vectors this task produces, and T-238, T-240 and T-241 build on them.

Version 1.0 cannot say what the real server needs to say:

1. `authorization-details-transaction.schema.json` describes one asset, one amount and one receiver. A group with two transfers, or a transfer plus an application call, cannot be shown truthfully, which breaks rule 1 (what you see is what you sign).
2. A value that cannot be priced has no representation; `fiat_value` is required.
3. `origin` is on the request but not in the signed object, so the receipt does not prove which site asked.
4. One device key cannot be both biometric-bound (audit finding BCHAT-007) and usable for per-call proofs and Deny (decision D9).
5. Settings loosening and approving a new device are signed by the device, with no schema or vector.
6. The docs disagree on `group_hash` (`sha256:…` in `docs/required-changes-biatec-oidc.md`, the group id in the spec).
7. Revision 2 of the architecture record (owner decisions of 2026-11-13): the operator vouches for no client and no application id, the user sets the threshold, and push is not Firebase. 1.0 has `verified`, an entity id and a display name on the client, and no way for the user to trust an application.

No production receipt exists, so 1.1 is additive where it can be and replaces 1.0 as the only version implementations produce.

## Goal

Spec, schemas, vectors and both reference verifiers describe protocol 1.1, and CI stays green without touching the mock, the backend or the app (they move to 1.1 in T-238, T-240, T-241).

## Scope

- In:
  - **`biatec_transaction` (additive):** new optional members `transfers` (array, at most 16, each `{ receiver, asset, amount, fiat_value? }`, only what the sender sends out), `applications` (array, at most 16, each `{ id: integer, trusted: boolean }`; `trusted` means the **user** trusted this application for this client on their phone (decision D16); there is no operator list), `value_known` (boolean), `origin` (absolute origin). `receiver`, `asset`, `amount` and `fiat_value` at the top level become optional. Rule in the spec (and an `anyOf` in the schema): a 1.1 producer always sends `transfers`, `applications`, `value_known` and `origin`; top-level `fiat_value` is the group total and is present exactly when `value_known` is true; the top-level single-transfer members are not produced any more. A renderer shows every transfer and every application, and "Value unknown" in the warning style when `value_known` is false.
  - **`group_hash`:** the Algorand group id recomputed from the handed transactions, base32 without padding, 52 characters; for one ungrouped transaction, its transaction id. State it in section 3.1.
  - **Subject:** section 8: `signed.sub` is the pairwise subject of the requesting client. The stored receipt's attestation is minted by Biatec OIDC for that subject at receipt time; the device presents its enrollment attestation and does not compare the request's `sub` with its own.
  - **Two keys (section 11.3 and 8.1):** approval key and presence key, each with a `kid` and an attestation carrying `biatec_key_use` (`approve` | `presence`). Verification step 3 gains: an `approve` receipt MUST carry an attestation with `biatec_key_use: "approve"`; `deny` and `code_failed` accept either. Device proofs are signed by the presence key.
  - **New signed objects** with schemas and vectors: `biatec_settings_change` (`{ type, sub, kid, nonce, issued_at, change }`, as in required-changes section 22), `biatec_device_approval` (`{ type, approver_kid, new_kid, new_approval_jwk_thumbprint, new_presence_jwk_thumbprint, pairing_code, nonce, issued_at }`), and the enrollment proof JWS `typ biatec-device-enroll+jwt` (`{ kid, presence_kid, presence_jwk_thumbprint, nonce, iat }`). Define the pairing code: the first 6 decimal digits of `SHA-256(new_approval_jwk_thumbprint | "." | new_presence_jwk_thumbprint | "." | nonce)` read as a big-endian number modulo 10^6, zero padded; give a worked example.
  - **Client identity (decision D13, revision 2):** the request's `client` object is exactly `{ fingerprint, domain_proven, domain?, linked }`. `fingerprint`: 8 lowercase hex characters of SHA-256 of the `client_id`, shown `xxxx-xxxx`. `domain`: present only when `domain_proven` is true; the ASCII host the client proved by serving `/.well-known/biatec-client.json` with its `client_id`. `linked`: whether the user already linked this client. Remove `verified`, `entity_id`, the display name and the logo from `consent-request.schema.json`, from the signed object and from the spec text; the spec states that Biatec OIDC vouches for no client and that a renderer shows the domain or "Unproven application" with the fingerprint, never a name.
  - **`remember` (decision D16):** the signed object of an `approve` receipt may carry `remember: { applications: [integer] }` (1 to 16 entries, each an application id of the request's `applications`). Verifiers refuse it on `deny` and `code_failed` and when an id is not in the request.
  - **Settings change members:** `biatec_settings_change.change` allows exactly `threshold_eur` (decimal string, at most 2 decimals, 0 or more; the schema has no ceiling, the threshold is the user's), `block_unproven_clients`, `biometric_required`, `ask_on_test_networks`, and under `clients.{client_id}`: `always_ask`, `per_client_threshold_eur` (string or null), `label` (at most 40 characters), `trusted_applications_add` and `trusted_applications_remove` (`[ { network, app_id } ]`), `paused` with `origin`, `unlink`. This is the list of AlgorandGoogleDriveAccount issue 30.
  - **Device channel (decision D12):** section on push: the server sends hints over its own SignalR hub (`consent_request`, `consent_closed`, `device_enrolled`, `settings_changed`, each with ids only); no Firebase. Shapes as AlgorandGoogleDriveAccount issue 29.
  - **Envelope `version`:** `"1.1"`.
  - **Vectors:** regenerate `vectors/receipts.json` in the 1.1 shape (keep the six cases, add: a two-transfer group, a transfer plus an application the user trusted, a transfer plus an untrusted application approved with `remember`, a value-unknown group, a deny signed by the presence key, and one negative vector: an approval signed by the presence key that verifiers must refuse). Add `vectors/device-signed.json` for the three new signed objects. Extend `vectors/keys.json` with a presence key (test-only).
  - **Reference verifiers:** `scripts/lib.mjs` (`verifyReceipt`, `verifyApproval`) and `backend/Biatec.Chat.Tests/Canonical/ConsentProtocolVectorTests.cs` enforce the key-use rule and pass every vector, including the negative one.
  - **`PRODUCT-DESCRIPTION.md`:** amend rule 6 and rule 7 and the Secure Area text for the two keys and for activation by a device-signed approval with a pairing code (the mock's `approved_by_kid` is not a proof); it is the specification, so the change is stated there first. Revision 2 amendments go into the same file in this task: (a) the threshold is the user's choice, 1000 EUR is the starting value and there is no deployment ceiling (the paragraph "The default policy is that any single action valued above 1000 EUR…", and "Tenant-level soft cap", whose floor becomes the default threshold); (b) no client is verified by a trust anchor or by the operator: the card shows the proven domain or the fingerprint, the 24 hour cooling-off applies to every newly registered client, the user links clients and trusts applications on the phone (rules 3 and 4, the tenant paragraph, the threat table rows that mention a trust anchor); (c) test networks have no operator price table and unknown application calls ask unless the user trusted the application (the Required Changes rows on valuation); (d) push is the SignalR device channel hosted by Biatec OIDC, not Firebase; (e) email notices go through Azure Communication Services.
- Out: the mock, backend, widget and app (T-237, T-238, T-240, T-241, T-242).

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [x] `pnpm --filter consent-protocol test` passes with the new vectors, including "altering any one signed member invalidates the signature" for the new objects.
- [x] The existing mock and conformance tests still pass unchanged (the schema change is additive for what they emit): `pnpm --filter mock-oidc test`.
- [x] `dotnet test backend --filter ConsentProtocolVectorTests` passes.
- [x] The spec has a "Changes from 1.0" section listing each point above.
- [x] `git grep -n -i "verified\|entity_id\|firebase\|fcm" packages/consent-protocol/spec packages/consent-protocol/schemas` finds only the "Changes from 1.0" section.
- [x] `PRODUCT-DESCRIPTION.md` no longer says the threshold cannot be raised above a ceiling, and no longer makes any behaviour depend on a trust anchor.
- [x] No private key other than the test-only vector keys is added.

## Implementation notes

- Vector generation script lives in `packages/consent-protocol/scripts/`; extend it, do not hand-edit signatures.
- Amounts stay decimal strings; `id` of an application is an integer at most 2^53 - 1.

## Verification

`pnpm --filter consent-protocol test && pnpm --filter mock-oidc test && dotnet test backend --filter ConsentProtocolVectorTests`

## Hand-off

**Branch:** `task/T-239` @ a7f9f64.

**What changed**
- **Schemas** (`packages/consent-protocol/schemas`): `authorization-details-transaction`, `consent-receipt-signed`, `consent-receipt` and `consent-request` now have a 1.1 branch and keep a 1.0 branch (`oneOf`, disjoint by required members), so 1.0 data keeps validating. New: `settings-change-signed`, `device-approval-signed`, `device-signed`, `device-enroll-proof`.
- **1.1 details:** `transfers` (at most 16), `applications` (id, `trusted`), `value_known`, top-level `fiat_value` exactly when the value is known, signed `origin`.
- **Signed object (1.1):** `client_fingerprint` (8 lowercase hex) in place of the 1.0 client member; optional `remember` (approve only). Envelope `version` "1.1", `device.key_use`.
- **Client identity:** the request's `client` is `{ fingerprint, domain_proven, domain?, linked }`; no name, logo or verified flag in 1.1.
- **Two keys:** attested `biatec_key_use`; an approve receipt must carry `approve`.
- **Pairing code:** first four bytes of SHA-256(approval thumbprint . presence thumbprint . nonce), big-endian, mod 10^6, six digits (this is how I read "the first 6 decimal digits ... modulo 10^6" in the task; worked example in the spec and in the vectors).
- **Vectors:** `receipts.json` is now 12 protocol 1.1 vectors (the six cases, a two-transfer group, a transfer plus a trusted application, an untrusted application approved with `remember`, a value-unknown group, a deny by the presence key, and one negative vector); the old set is frozen as `receipts-1.0.json`. New `device-signed.json` (settings changes, device approvals, enrollment proofs, pairing code; three negative ones). `keys.json` gains the presence key and a second device's two keys (test-only). The generator was rewritten for this; it leaves `canonicalization.json` alone.
- **Verifiers:** `scripts/lib.mjs` (`verifyReceipt` with the key-use rule, `attestedKeyUse`, `verifyDeviceSigned`, `verifyEnrollmentProof`, `pairingCode`, client fingerprint in `verifyApproval`) and `ConsentProtocolVectorTests.cs` (every vector, including the refused ones, plus pairing code and enrollment proofs).
- **Spec:** rewritten for 1.1 with sections 8.2 (device-signed statements), 8.3 (enrollment proof and pairing code) and 13 "Changes from 1.0".
- **PRODUCT-DESCRIPTION.md:** the threshold is the user's (no ceiling by default; soft-cap floor is the default threshold), no client or application is vouched for by the operator (rules 3 and 4, the tenant paragraph, the integration steps, the valuation rows, the Required Changes rows), two keys (rule 6, authenticator bullets), activation by a signed approval with a pairing code (rule 7), and the device channel instead of Firebase.

**Verification** (all run): `node --test` in `packages/consent-protocol`: 113 passed; `pnpm --filter mock-oidc test`: 34 passed, and the conformance kit: 23 passed, both without any change to them; `dotnet test backend --filter ConsentProtocolVectorTests`: 9 passed.

**Deviations from the task text (for the reviewer)**
- The acceptance line "`git grep verified|entity_id|firebase|fcm` in spec and schemas finds only Changes from 1.0" is met for the spec text and for every 1.1 branch, but the schemas keep the 1.0 members inside their `v1_0` / `clientV1_0` branches. Removing them would break the mock and the conformance kit, which the task requires to pass unchanged (T-240 moves them to 1.1). A test enforces exactly this boundary.
- The first vector of `receipts.json` changed shape (1.1); `spec.test.mjs` already compared the spec's examples with it, so the spec examples changed too.

**Not done here** (other tasks): the mock, backend, widget and app still produce 1.0 (T-238, T-240, T-241, T-242). Test-only private keys are the only private keys added (a test asserts nothing else in `vectors/` holds a `d` member).

**Unblocks:** T-238, T-240.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → backlog · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-13 09:05 · scholtz · backlog → backlog · amended for revision 2 of the architecture record (user-held trust, user-set threshold, device channel instead of Firebase)
- 2026-11-13 15:11 · scholtz · backlog → in-progress · taken and claimed (session-20261113-1511), branch task/T-239
- 2026-11-13 15:40 · scholtz · in-progress → review · PR for task/T-239
