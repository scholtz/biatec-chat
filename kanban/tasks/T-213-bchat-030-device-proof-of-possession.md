---
id: T-213
title: "Authenticator: prove possession of the device key on every device call; rate-limit push re-registration (BCHAT-030)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/lib/security/device_proof.dart", "apps/biatec_authenticator/lib/approvals/**", "apps/biatec_authenticator/lib/security/settings_client.dart", "packages/consent-protocol/mock-oidc/**", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#283"
branch: "task/T-213"
owner: "session-20261023-1858"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-213 - Authenticator: prove possession of the device key on every device call; rate-limit push re-registration (BCHAT-030)

## Context

Security audit finding BCHAT-030 (Low-Medium, issue #283, MOB-6, RISK-003). Every device call authenticates with `Authorization: Device <token>`, a long-lived bearer. Whoever copies the token (a backup, a log, a compromised build) can list the user's requests and re-register the push target (`PUT /devices/push`) to receive the user's notifications, without owning the device key.

## Goal

A device call is accepted only with a short-lived proof signed by the device key (DPoP-style: method, URL, time, a unique id and the hash of the token), the mock enforces it when asked to, and a push re-registration is rate limited.

## Scope

- In: `DeviceProofSigner` in the authenticator (ES256 JWS `X-Device-Proof`), attached by the approval and settings clients; the mock verifies it when `deviceProof` is on (server env `MOCK_DEVICE_PROOF=1`), with replay protection; a per-device limit on push registrations in the mock; the requirement written into docs/required-changes-biatec-oidc.md (a change at the real Biatec OIDC).
- Out: turning the proof on by default in the mock (the Dart contract tests and the conformance kit still use the bearer alone until the real server asks for it); biometric gating of the key (BCHAT-007), which needs a second, ungated key for these proofs.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] The proof is an ES256 compact JWS with header typ `biatec-device-proof+jwt` and the device kid and payload htm, htu, iat, jti, ath (base64url SHA-256 of the device token), signed by the keystore key.
- [x] With the mock's proof mode on, a call without a proof, with a wrong method, URL, token hash, key, an old iat or a replayed jti is refused (401); a correct one passes.
- [x] The authenticator's approval and settings clients send the proof when a signer is configured, and the app configures one.
- [x] More than 6 push registrations per minute for one device are refused (429) by the mock.
- [x] Mock, conformance and `flutter test` suites pass.

## Verification

`pnpm --filter mock-oidc test`, `pnpm --filter @biatec/consent-protocol conformance`, `flutter analyze` and `flutter test`.

## Hand-off

- **Summary:** authenticator: `DeviceProofSigner` / `KeystoreDeviceProofSigner` (ES256 JWS `X-Device-Proof`: typ, kid, htm, htu, iat, jti, ath) attached by `ApprovalClient` and `SettingsClient`, wired in `ApprovalServices.fromConfig`; `crypto` is now a direct dependency. Mock: `deviceProof` option (`MOCK_DEVICE_PROOF=1`) verifies it with replay protection, and `PUT /devices/push` is limited to 6 a minute per device. docs/required-changes-biatec-oidc.md has the section for the real Biatec OIDC.
- **Verification:** `pnpm --filter mock-oidc test`: 32 pass (2 new); `flutter analyze` clean; `flutter test`: all pass (3 new + a contract test that I ran against a mock started in proof mode: the bearer alone is refused, the proof opens the call).
- **Not done / for the owner:** this does not protect against the real server until Biatec OIDC requires the proof (the issue stays about that dependency); the biometric gating of the key (BCHAT-007) needs a second key for these proofs.

## Review

## Activity log

- 2026-10-23 18:58 · kanban-split · (new) → backlog · created from audit issue #283
- 2026-10-23 18:58 · scholtz · taken · status stays backlog
- 2026-10-23 18:58 · session-20261023-1858 · backlog → in-progress · claimed, branch task/T-213
