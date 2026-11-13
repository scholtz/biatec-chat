---
id: T-240
title: "Align the mock, the backend's signing path and the authenticator card with protocol 1.1 and the real Biatec OIDC"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-239]
area: ["packages/consent-protocol/mock-oidc/**", "packages/consent-protocol/conformance/**", "backend/Biatec.Chat.Tools/Signing/**", "backend/Biatec.Chat.Tools/Caps/**", "backend/Biatec.Chat.Api/Tenancy/**", "backend/Biatec.Chat.Api/Chat/Confirm/**", "backend/Biatec.Chat.Tests/Chat/**", "apps/biatec_authenticator/lib/approvals/approval_card.dart", "apps/biatec_authenticator/lib/approvals/models/**", "apps/biatec_authenticator/test/approvals/**", "packages/chat-widget/src/i18n/**", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#400"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#biatec-authenticator-and-the-biatec-consent-protocol"
created: 2026-11-12
updated: 2026-11-13
---

# T-240 - Align the mock, the backend's signing path and the authenticator card

## Context

Architecture record: ludovit-scholtz/AlgorandGoogleDriveAccount#23 (decisions D3, D5, D6, D14; phase 1, step 6). The real signing endpoint is specified in ludovit-scholtz/AlgorandGoogleDriveAccount#28.

The mock (`packages/consent-protocol/mock-oidc/src/app.ts`) is the contract everything here is tested against, and it differs from what the real server will do:

- a group is `base64(JSON)` per transaction, not Algorand msgpack, so a real group from the backend does not decode and no group id is checked;
- it has no `GET /consent/settings`, which the backend calls (`ConsentSettingsClient`);
- it emits the 1.0 single-transfer details;
- it has none of the real refusals (`consent_unavailable`, `client_paused`, `daily_cap_exceeded`, …).

The backend's `WalletSigningClient` maps any refusal to `Rejected`, and `docs/required-changes-biatec-oidc.md` shows a 4 digit code, a `sha256:` group hash, `/connect/token` and a receipt shape that are all out of date.

## Goal

The mock behaves like the real server for the signing round trip, and the backend, the widget texts and the authenticator card handle everything the real server can answer.

## Scope

- In:
  - **Mock.** Decode real unsigned Algorand transactions (`algosdk`), recompute the group id and refuse an inconsistent `grp` (`400 group_hash_mismatch`); emit 1.1 `authorization_details` (`transfers`, `applications`, `value_known`, `origin`, total `fiat_value`); `GET /consent/settings` (bearer) as AlgorandGoogleDriveAccount issue 27 defines it, including `default_threshold_eur`, `ask_on_test_networks`, `client.linked`, `client.domain_proven` and `client.trusted_applications`; the client object of a request is `{ fingerprint, domain_proven, domain?, linked }` (T-239); the refusals of issue 28 (`403 consent_unavailable` with `reason: no_active_device`, `403 client_paused`, `403 unproven_client_blocked`, `403 daily_cap_exceeded`, `403 consent_unsupported_operation`, `403 consent_unsupported_network`, `403 consent_hash_mismatch`, `429 too_many_pending_requests`, `429 rate_limited`), reserve-then-spend (a second use of an elevated token answers `401` with `error_description: "consent not usable"`), `valuedEur` in the signing answer, and the consent token refused on every other endpoint. Keep the JSON-group mode behind `MOCK_JSON_GROUPS=1` only if a consumer still needs it; otherwise remove it.
  - **Conformance kit.** Cases for each of the above.
  - **Backend.** `WalletSigningClient`: parse the refusal's `error`, `reason` and `cap_eur` into a typed `Rejected` outcome. `ConfirmService`: map each to a user-facing code the widget can render (reuse `no_active_device` and `no_device_daily_cap`; add `client_paused`, `unproven_client_blocked`, `consent_unsupported`, `too_many_pending`); a `401 consent not usable` on the resume path starts one new confirm round, never a loop. The 429s keep "try later". Send `origin` and `maxConsentSeconds` as today.
  - **The user's threshold.** The prediction uses the user's `threshold_eur` from `GET /consent/settings` as it is: it may be above 1000 or 0. Remove the clamp in `backend/Biatec.Chat.Tools/Caps/NoDevice/NoDeviceCap.cs` (`Math.Min(s.ThresholdEur ?? ceiling, ceiling)`, line 104) and fix its doc comment (line 29). `CapDefaults.ConsentThresholdCeilingEur` and the tenant's `ConsentThresholdCeilingEur` keep their names and now mean "the default threshold": used when the settings endpoint answers `404`, and as the floor of the soft cap. No migration.
  - **Domain proof.** `GET /.well-known/biatec-client.json` on the backend answers `{ "client_ids": ["<the OIDC client id of the tenant bound to the request host>"] }`, `Content-Type: application/json`, `Cache-Control: public, max-age=3600`, nothing else, and `404` for an unknown host. This is how the phone can show the tenant's domain (decision D13); without it the card says "Unproven application".
  - **Widget texts** for the new codes, English and Slovak (strings only; behaviour is T-237).
  - **`errorCode` on the confirm answer.** Add `ErrorCode` to `ConfirmResponse` and send it from `ConfirmEndpoints.ToHttp` and `ConsentEndpoints.ToResponse` (today `ConfirmResult.ErrorCode` is dropped), and carry the reason of a wrong-code denial on the consent stream. The widget (T-237, merged) already reads `errorCode` and shows its own text for `no_active_device`, `no_device_daily_cap`, `code_failed`, `preview_expired`, `resume_failed` and `receipt_missing`; until the backend sends it, those cases show the backend's message. Add a backend test per code.
  - **Authenticator card** (`approval_card.dart`): render every entry of `transfers` (receiver, amount, asset, EUR value) and of `applications` (id, and a warning style when `trusted` is false), the client as its proven domain or "Unproven application" with the fingerprint (never a name), the group total or "Value unknown" in the warning style, and the `origin`. A request without `transfers` (1.0 shape) is refused with "This request cannot be shown; update the app" and cannot be approved. Remove any comparison of the request's `sub` with the device's own subject if one exists.
  - **`docs/required-changes-biatec-oidc.md`.** Six-digit code in every example, the 1.1 receipt envelope, `/token`, `group_hash` as the group id, the new refusals, the stale "Mock status" paragraph, the push section (section 4: the SignalR device channel of AlgorandGoogleDriveAccount issue 29 replaces Firebase; the stored Firebase installation id is gone), the client identity rows (proven domain, no verified flag), the valuation rows (no operator price table or application list), and for each row the Biatec OIDC issue that implements it (rows 1 to 4, 8, 10, 11, 16, 17, 19 to 22, 24, 25 and the device-proof section: AlgorandGoogleDriveAccount issues 24 to 36).
- Out: receipt verification (T-238), the widget's following of the consent (T-237), the two keys (T-241), enrollment (T-242), flipping production flags (T-243).

## Parallel work

Parallel work possible: **yes**. T-238 changes `backend/Biatec.Chat.Api/Consent/**` and `Biatec.Chat.Tools/Consent/**`; this task stays in `Signing/**` and `Chat/Confirm/**`.

## Acceptance criteria

- [ ] A real two-transaction Algorand group built with `algosdk` round-trips through the mock: challenge, receipt, token, signed; changing one byte after approval gives `consent_hash_mismatch`.
- [ ] Each refusal has a conformance case and a backend test showing the user-facing code.
- [ ] `401 consent not usable` on resume leads to exactly one new challenge in a test, then stops.
- [ ] Backend tests: a user whose threshold is 5000 gets no phone prediction for a 2000 EUR transfer; a threshold of 0 predicts the phone for every action; `404` from the settings endpoint falls back to the tenant default.
- [ ] `GET /.well-known/biatec-client.json` returns the tenant's client id for its host and `404` for another host.
- [ ] The card widget tests render a two-transfer group, an untrusted application, "Value unknown", a proven domain and an unproven application; a 1.0 request is not approvable.
- [ ] `pnpm --filter mock-oidc test`, the conformance kit, `dotnet test backend`, `flutter test` in `apps/biatec_authenticator`, and the widget's `vitest` and `vue-tsc` pass.

## Implementation notes

- `algosdk` is already a workspace dependency of the tools packages; pin the same version in the mock.
- Keep the mock's seed users (alice, bob, carol) and add one whose client is paused, one in cooling-off and one who raised the threshold to 5000.

## Verification

`pnpm --filter mock-oidc test && pnpm --filter conformance test && dotnet test backend && (cd apps/biatec_authenticator && flutter test)`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-12 18:43 · scholtz · (new) → blocked · created from the consent architecture record (AlgorandGoogleDriveAccount issue 23)
- 2026-11-12 18:43 · kanban-board · backlog → blocked · waiting on: T-239
- 2026-11-13 09:05 · scholtz · blocked → blocked · amended for revision 2 of the architecture record (user's threshold not clamped, proven domain instead of verified, well-known file)
- 2026-11-13 14:20 · scholtz · scope note · added the errorCode on ConfirmResponse (found while merging T-237)
