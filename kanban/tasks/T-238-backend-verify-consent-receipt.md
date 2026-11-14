---
id: T-238
title: "Backend: verify the consent receipt before resuming, and keep denial receipts as evidence"
status: done
priority: P0
epic: "Consent & authenticator"
blocked_by: [T-239]
area: ["backend/Biatec.Chat.Api/Consent/**", "backend/Biatec.Chat.Tools/Consent/**", "backend/Biatec.Chat.Tests/Consent/**", "backend/Biatec.Chat.Api/Evidence/**"]
parallel: yes
conflicts_with: []
issue: "#398"
branch: "task/T-238"
owner: "session-20261114-0830"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-12
updated: 2026-11-14
---

# T-238 - Backend: verify the consent receipt before resuming

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23 (decisions D2, D9, D15; phase 1, step 6).

`ConsentOrchestrator.ApprovedAsync` (`backend/Biatec.Chat.Api/Consent/ConsentOrchestrator.cs`, lines 114 to 152) stores the `consent_receipt` of the CIBA token response as received and resumes signing. No production code checks the device signature, the attestation, or that the receipt is about this preview. Verification exists only in tests (`Biatec.Chat.Tests/Canonical/ConsentProtocolVectorTests.cs`) and in JavaScript (`packages/consent-protocol/scripts/lib.mjs`, `verifyReceipt` and `verifyApproval`). The evidence chain therefore records "approved on the phone" on Biatec OIDC's word alone, and a receipt for another action would be recorded as evidence for this one. Spec: `packages/consent-protocol/spec/biatec-consent-protocol.md` section 8.1, steps 1 to 5.

## Goal

The backend resumes signing only with a receipt it verified itself against what it asked for, and every decision (approve, deny, code failed) leaves a verified, signed receipt in the evidence chain.

## Scope

- In:
  - `ConsentReceiptVerifier` in `Biatec.Chat.Tools/Consent/` (production code; move the logic out of the vector test, which then calls it). Returns a result enum, never throws on malformed input. Steps:
    1. Strict shape: exactly the envelope and `signed` members of the 1.1 schemas (T-239); `alg` ES256; signature 64 bytes base64url without padding.
    2. Attestation: compact JWS, `typ biatec-device-attestation+jwt`, ES256, signed by an EC P-256 key of the Biatec OIDC JWKS (the JWKS the backend already loads through discovery; cache as today, refetch once on an unknown `kid`), `iss` = `Oidc:Issuer`, `exp` in the future, `sub == signed.sub`, `device_kid == device.kid`, thumbprint (RFC 7638) of `device.public_key`, and for an `approve` receipt `biatec_key_use == "approve"`.
    3. Device signature over RFC 8785 of `signed` (the backend `JsonCanonicalizer`).
    4. Match against the stored `ConsentRequest` and preview, never against the receipt's own data: `auth_req_id`; `client_entity_id` equals the tenant's configured entity id (new tenant setting `OidcClientEntityId`, default the `OidcClientId`); `sub` equals the session's `sub`; `binding_code` equals the stored code in constant time; `expiry` not passed; `binding_message` is the empty string (step-up entry); `authorization_details` has exactly one `biatec_transaction` whose `group_hash` equals the base32 group id the backend computed for the preview's group, whose `network` and `sender` equal the preview's, and whose `origin` equals the stored origin.
    5. `decision == "approve"` for the approval path.
  - `ApprovedAsync`: verify first. Any failure → the step ends as `receipt_invalid` (same handling as `receipt_missing`: cancelled, the elevated token is discarded and never used), with an evidence event `consent_receipt_rejected` that carries the failure code and the receipt's SHA-256, not the receipt.
  - Denied and expired: fetch `GET /consent/receipts/{auth_req_id}` (bearer: the session's access token), verify steps 1 to 4 with `decision` `deny` or `code_failed`, and record it with `EvidencePayloads.ConsentReceipt`. If it cannot be fetched or verified, record `consent_denied_unverified` and still cancel the action (a denial is always honoured).
  - `Consent:VerifyReceipts` (default true). Setting it false is logged as a warning at startup; it exists only so a broken deployment can be diagnosed.
  - `packages/evidence-verifier`: accept the 1.1 receipt shape if it validates receipts.
- Out: the widget (T-237), error codes of the signing call (T-240).

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [x] Every 1.1 vector verifies; each of these alterations is refused with its own failure code: one changed signed member, a signature by another key, an attestation for another `sub`, an expired attestation, an attestation signed by a key not in the JWKS, `biatec_key_use: "presence"` on an approval, another `auth_req_id`, another group hash, another sender, another origin, a wrong binding code, `decision: "deny"` on the approval path.
- [x] `ConsentOrchestratorTests`: a valid receipt resumes; each invalid receipt ends the step as `receipt_invalid`, the signer is not called again, and the evidence event is written.
- [x] A denial records a verified denial receipt; an unfetchable one records `consent_denied_unverified` and still cancels.
- [x] The test fakes build real signed receipts with the vector test keys (`packages/consent-protocol/vectors/keys.json`); no dummy receipt strings remain in the consent tests.
- [x] `dotnet test backend` passes.

## Implementation notes

- The group id: the backend already recomputes it before calling the signing endpoint (required-changes row 8 fallback); use that value, encoded base32 without padding.
- Constant-time comparison: `CryptographicOperations.FixedTimeEquals`.

## Verification

`dotnet test backend`

## Hand-off

PR #410, `task/T-238` @ b105aef. `dotnet test backend` passes (CI `backend` green).

- `ConsentReceiptVerifier` (Tools) verifies shape, attestation against the Biatec OIDC JWKS, device signature and the match against the stored request; both receipt versions ("1.1" and the frozen "1") because the mock and real OIDC emit 1.0 until T-240. `ConsentReceiptService` (Api) builds the expectation, refetches the JWKS once on an unknown `kid`, fetches denial receipts.
- A bad approval receipt ends the step as `receipt_invalid` with a hash-only `consent_receipt_rejected` event; a denial records a verified receipt or `consent_denied_unverified` and always cancels. `Consent:VerifyReceipts` (default true). Tenant `OidcClientEntityId` (nullable column + migration).
- Known limits: no active-device lookup (the backend has none; a pending device's receipt verifies); `remember` is checked for shape only; expiry (nobody decided) records no receipt; `OidcClientEntityId` has no admin UI yet (set in the database; the 1.1 fingerprint uses the `client_id`); the receipt expiry has a grace of one poll interval.
- Review: 6 rounds of `/code-review high`; real defects fixed (1.1 fingerprint input, expiry grace scope, denial claim race, cancellation after the one-time token is spent, redirect handling on the receipt client).

**Unblocks:** T-243 still waits on T-240.

## Review

- 2026-11-14 14:18 · approve · PR #410 merged into main @ f693ebe (recorded by kanban-sync)

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-239
- 2026-11-14 13:21 · kanban-board · blocked → backlog · blockers done: T-239
- 2026-11-14 08:30 · scholtz · backlog → in-progress · claimed by session-20261114-0830 on task/T-238
- 2026-11-14 14:20 · scholtz · in-progress → review · PR #410 @ b105aef
- 2026-11-14 14:18 · user · review → done · PR #410 merged into main @ f693ebe (synced by kanban-sync)
