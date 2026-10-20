# mock-oidc

Local stand-in for the consent side of Biatec OIDC (we cannot change the real one). Used by backend tests, the
authenticator and the conformance suite. Plain Node 22.18+ (native TypeScript), no framework. Validates every request
and response against `../schemas`, and reuses `../scripts/lib.mjs` for receipt verification, so it stays consistent
with the vectors.

```
pnpm --filter mock-oidc test        # tests (rules 2, 4, 5, 7, 8, 9, 10)
pnpm --filter mock-oidc start       # http://127.0.0.1:8089 (PORT, HOST)
docker build -f packages/consent-protocol/mock-oidc/Dockerfile -t biatec-mock-oidc .   # from the repo root
```

## Endpoints

| Endpoint | Purpose |
| --- | --- |
| `POST /wallet/{network}/{address}/sign` | Step-up entry: 401 `insufficient_user_authentication` with `auth_req_id` and `binding_code`; with a consent token it checks the group hash and spends the consent |
| `PUT /wallet/limits` | Tightening applies at once; loosening is a consent-elevated step-up bound to the body hash |
| `POST /connect/ciba` | Direct entry (client auth: Basic or form) |
| `POST /token` | CIBA grant (`authorization_pending`, `access_denied`, `expired_token`), returns `consent_receipt` |
| `GET /consent/receipts/{auth_req_id}` | Receipt, owning client only |
| `POST /devices` | Enrollment with a `login-*` token; inactive 24 h unless `approved_by_kid` names an active device |
| `POST /devices/recovery-code`, `POST /devices/recovery-code/ack` | `Authorization: Device <token>`; the recovery code is issued only once the device is active (403 while inactive), re-issued until acknowledged, then 409 (rule 7) |
| `GET /devices/requests`, `GET /consent/requests/{id}`, `POST /consent/requests/{id}/receipt` | Authenticator side, `Authorization: Device <token>`; the request never contains the binding code |
| `GET /.well-known/jwks.json`, `/.well-known/openid-configuration` | Discovery |
| `PUT /devices/push` | `Authorization: Device <token>`; registers or replaces the device's push token |
| `GET /devices`, `POST /devices/{kid}/cancel` | Device list; cancelling a pending enrollment (T-084) |
| `GET /devices/settings`, `PUT /devices/settings` | Consent settings; loosening needs a device-signed `biatec_settings_change` (T-084) |
| `GET /mock/devices/{kid}`, `POST /mock/devices/{kid}/activate`, `GET /mock/push-outbox?kid=...` | Test helpers; the outbox shows the push messages that would be sent |

## Rules implemented

- 2: six-digit binding code, three attempts in total, the third wrong code is `code_failed`.
- 4: first use of a client always needs the phone; `requireConsent` and "always ask" force it.
- 5: 300 s validity, bound to one group hash (mismatch refused and the consent stays unspent), single use (spent only when signing succeeds).
- 7, 8: first device inactive for 24 h; no active device means no above-threshold action (403 `reason: no_active_device`).
- 9: the device receipt and its attestation are verified with the published JWKS and the signed members are compared with the stored request.
- 10: an explicit Deny gives a signed `deny` receipt and `access_denied`.

## Seed data (public test data)

- Issuer `https://oidc.biatec.test`; signing key and device key are the test-only keys in `../vectors/keys.json`.
- Clients (Basic auth): `biatec-chat` / `biatec-chat-secret` (verified, may use `manage-limits`), `bank-example` / `bank-example-secret` (verified; first use needs the phone), `unverified-app` / `unverified-app-secret`.
- Users: `alice@example.com` (active device `alice-device`, 1000 EUR threshold), `carol@example.com` (active device `carol-device`, always asks for `biatec-chat`), `bob@example.com` (no device).
- Bearer tokens: `alice-chat-sign`, `alice-chat-limits`, `alice-bank-sign`, `alice-unverified-sign`, `carol-chat-sign`, `bob-chat-sign`, `alice-chat-nosign`; login tokens `login-alice`, `login-bob`. Seeded device tokens: `Device device-token-alice`, `Device device-token-carol`.

## Mocked simplifications

The unsigned group is `base64(JSON)` per transaction (`sender`, `receiver`, `asset`, `amount`, `fiat_eur`, optional `application_id`, see `src/group.ts`), not msgpack; the group hash only has the shape of the real one. State is in memory. Nothing is sent to FCM or APNs: each new request queues one message per active device with a push
token (`GET /mock/push-outbox`), and clients can also poll `GET /devices/requests`. Per-origin pending-request limits and denial pauses (rule 10) are not modelled.
