---
id: T-239
title: "Consent protocol 1.1: whole-group details, signed origin, two device keys, device-signed settings and device approval"
status: backlog
priority: P0
epic: "Consent & authenticator"
blocked_by: []
area: ["packages/consent-protocol/spec/**", "packages/consent-protocol/schemas/**", "packages/consent-protocol/vectors/**", "packages/consent-protocol/scripts/**", "packages/consent-protocol/test/**", "backend/Biatec.Chat.Tests/Canonical/**", "PRODUCT-DESCRIPTION.md"]
parallel: yes
conflicts_with: []
issue: "#399"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-12
updated: 2026-11-12
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

No production receipt exists, so 1.1 is additive where it can be and replaces 1.0 as the only version implementations produce.

## Goal

Spec, schemas, vectors and both reference verifiers describe protocol 1.1, and CI stays green without touching the mock, the backend or the app (they move to 1.1 in T-238, T-240, T-241).

## Scope

- In:
  - **`biatec_transaction` (additive):** new optional members `transfers` (array, at most 16, each `{ receiver, asset, amount, fiat_value? }`, only what the sender sends out), `applications` (array, at most 16, each `{ id: integer, known: boolean }`), `value_known` (boolean), `origin` (absolute origin). `receiver`, `asset`, `amount` and `fiat_value` at the top level become optional. Rule in the spec (and an `anyOf` in the schema): a 1.1 producer always sends `transfers`, `applications`, `value_known` and `origin`; top-level `fiat_value` is the group total and is present exactly when `value_known` is true; the top-level single-transfer members are not produced any more. A renderer shows every transfer and every application, and "Value unknown" in the warning style when `value_known` is false.
  - **`group_hash`:** the Algorand group id recomputed from the handed transactions, base32 without padding, 52 characters; for one ungrouped transaction, its transaction id. State it in section 3.1.
  - **Subject:** section 8: `signed.sub` is the pairwise subject of the requesting client. The stored receipt's attestation is minted by Biatec OIDC for that subject at receipt time; the device presents its enrollment attestation and does not compare the request's `sub` with its own.
  - **Two keys (section 11.3 and 8.1):** approval key and presence key, each with a `kid` and an attestation carrying `biatec_key_use` (`approve` | `presence`). Verification step 3 gains: an `approve` receipt MUST carry an attestation with `biatec_key_use: "approve"`; `deny` and `code_failed` accept either. Device proofs are signed by the presence key.
  - **New signed objects** with schemas and vectors: `biatec_settings_change` (`{ type, sub, kid, nonce, issued_at, change }`, as in required-changes section 22), `biatec_device_approval` (`{ type, approver_kid, new_kid, new_approval_jwk_thumbprint, new_presence_jwk_thumbprint, pairing_code, nonce, issued_at }`), and the enrollment proof JWS `typ biatec-device-enroll+jwt` (`{ kid, presence_kid, presence_jwk_thumbprint, nonce, iat }`). Define the pairing code: the first 6 decimal digits of `SHA-256(new_approval_jwk_thumbprint | "." | new_presence_jwk_thumbprint | "." | nonce)` read as a big-endian number modulo 10^6, zero padded; give a worked example.
  - **Unverified clients:** `client.fingerprint` format: 8 lowercase hex characters of SHA-256 of the `client_id`, shown `xxxx-xxxx`.
  - **Envelope `version`:** `"1.1"`.
  - **Vectors:** regenerate `vectors/receipts.json` in the 1.1 shape (keep the six cases, add: a two-transfer group, a transfer plus a known application, a value-unknown group, a deny signed by the presence key, and one negative vector: an approval signed by the presence key that verifiers must refuse). Add `vectors/device-signed.json` for the three new signed objects. Extend `vectors/keys.json` with a presence key (test-only).
  - **Reference verifiers:** `scripts/lib.mjs` (`verifyReceipt`, `verifyApproval`) and `backend/Biatec.Chat.Tests/Canonical/ConsentProtocolVectorTests.cs` enforce the key-use rule and pass every vector, including the negative one.
  - **`PRODUCT-DESCRIPTION.md`:** amend rule 6 and rule 7 and the Secure Area text for the two keys and for activation by a device-signed approval with a pairing code (the mock's `approved_by_kid` is not a proof); it is the specification, so the change is stated there first.
- Out: the mock, backend, widget and app (T-237, T-238, T-240, T-241, T-242).

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [ ] `pnpm --filter consent-protocol test` passes with the new vectors, including "altering any one signed member invalidates the signature" for the new objects.
- [ ] The existing mock and conformance tests still pass unchanged (the schema change is additive for what they emit): `pnpm --filter mock-oidc test`.
- [ ] `dotnet test backend --filter ConsentProtocolVectorTests` passes.
- [ ] The spec has a "Changes from 1.0" section listing each point above.
- [ ] No private key other than the test-only vector keys is added.

## Implementation notes

- Vector generation script lives in `packages/consent-protocol/scripts/`; extend it, do not hand-edit signatures.
- Amounts stay decimal strings; `id` of an application is an integer at most 2^53 - 1.

## Verification

`pnpm --filter consent-protocol test && pnpm --filter mock-oidc test && dotnet test backend --filter ConsentProtocolVectorTests`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → backlog · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
