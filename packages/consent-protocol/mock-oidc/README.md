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
| `POST /wallet/{network}/{address}/sign` | Body `transactions` (base64 msgpack unsigned Algorand transactions; `unsigned_group` is accepted too), optional `grp`, `requireConsent`, `maxConsentSeconds`, `origin`. Decodes with algosdk and recomputes the group id (`400 group_hash_mismatch`). Below the user's policy it signs and answers `signedTransactions` (zero signatures) and `valuedEur`; otherwise 401 `insufficient_user_authentication` with `auth_req_id` and `binding_code`. With a consent token it checks `consent_hash`, reserves, signs, spends; a second use is `401 consent not usable`. Refusals: `403 consent_unsupported_operation`, `consent_unsupported_network`, `consent_unavailable` (`reason: no_active_device`), `client_paused`, `unproven_client_blocked`, `daily_cap_exceeded` (`cap_eur`, `reason`), `consent_hash_mismatch`; `429 too_many_pending_requests`, `429 rate_limited` |
| `GET /consent/settings` | Bearer (a consent token is refused): `threshold_eur`, `default_threshold_eur`, `ask_on_test_networks`, `device_enrolled`, `device_active` and the calling client's `linked`, `always_ask`, `paused_until`, `cooling_off_until`, `first_use`, `per_client_threshold_eur`, `domain_proven`, `trusted_applications` |
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
| `GET /mock/devices/{kid}` (the answer also carries the device's `presence` key and its attestation), `POST /mock/devices/{kid}/activate`, `GET /mock/push-outbox?kid=...` | Test helpers, served only with `MOCK_TEST_HELPERS=1` (always on when the mock is embedded in tests); the outbox shows the push messages that would be sent. Without the flag the seeded devices also get random tokens, because `device-token-<user>` is predictable (BCHAT-006) |

## Running it

`pnpm --filter mock-oidc start` binds to `127.0.0.1:8089`. The Docker image does too; to reach it through a published port run
`docker run -p 127.0.0.1:8089:8089 -e HOST=0.0.0.0 biatec-mock-oidc` (the publish address keeps it on the host's loopback). Set
`MOCK_TEST_HELPERS=1` only for local contract tests and never on a mock others can reach. On Windows PowerShell:
`$env:MOCK_TEST_HELPERS = "1"; pnpm --filter mock-oidc start`.

## Rules implemented

- 1: what you see is what you sign: `authorization_details` list every transfer and application (protocol 1.1); the receipt must restate them.
- 2: six-digit binding code, three attempts in total, the third wrong code is `code_failed`.
- 3, 4: the client is named by its fingerprint and proven domain only; first use of a client, a client registered less than 24 hours ago, `requireConsent` and "always ask" always need the phone; without a device the first day with a client has a reduced cap of 100 EUR.
- 5: 300 s validity, bound to one group hash (mismatch refused and the consent stays unspent), single use (spent only when signing succeeds).
- 7, 8: first device inactive for 24 h; no active device means no above-threshold action (403 `reason: no_active_device`).
- 9: the device receipt and its attestation are verified with the published JWKS and the signed members are compared with the stored request.
- 10: an explicit Deny gives a signed `deny` receipt and `access_denied`; at most three pending requests per client, user and origin and 60 requests an hour.

## Seed data (public test data)

- Issuer `https://oidc.biatec.test`; signing key and device key are the test-only keys in `../vectors/keys.json`.
- Clients (Basic auth): `biatec-chat` / `biatec-chat-secret` (proven domain `chat.biatec.io`, may use `manage-limits`), `bank-example` / `bank-example-secret` (proven domain; first use needs the phone), `new-app` / `new-app-secret` (proven domain, registered an hour ago: cooling-off), `unverified-app` / `unverified-app-secret` (no proven domain). A client's fingerprint is the first 16 hex characters of the SHA-256 of its id.
- Users: `alice@example.com` (active device `alice-device`, 1000 EUR threshold), `carol@example.com` (active device, always asks for `biatec-chat`), `bob@example.com` (no device), `dave@example.com` (active device, paused `biatec-chat`: `client_paused`), `erin@example.com` (no device and no earlier signature: the reduced cap, `daily_cap_exceeded`), `frank@example.com` (active device, threshold 5000 EUR), `gina@example.com` (active device, blocks clients without a proven domain). A device has an approval key (`<name>-device`, the vector `device` key) and a presence key (`<name>-device-presence`, the vector `presence` key), each attested for its own use.
- Bearer tokens: `alice-chat-sign`, `alice-chat-limits`, `alice-bank-sign`, `alice-new-sign`, `alice-unverified-sign`, `carol-chat-sign`, `bob-chat-sign`, `dave-chat-sign`, `erin-chat-sign`, `frank-chat-sign`, `gina-chat-sign`, `gina-unverified-sign`, `alice-chat-nosign`; login tokens `login-alice`, `login-bob`. Seeded device tokens: `Device device-token-<name>`.

## Mocked simplifications

- Groups are real unsigned Algorand transactions (`algosdk` 3.9.0, base64 msgpack). `src/build.ts` builds them for tests (`buildGroup`, `usdcTransfer`, `account`). "Signing" returns the transactions with an all-zero signature. Bitcoin cannot be decoded: a Bitcoin request is `403 consent_unsupported_network`. `MOCK_JSON_GROUPS=1` (option `jsonGroups`) switches to base64(JSON) descriptors (`src/group.ts`, `Tx`); only the e2e demo harness uses it.
- Valuation is a static price table in `src/group.ts` (ALGO 0.20, USDC 0.9225 EUR, on mainnet and testnet alike); any other asset is unpriced, so the value is unknown and the phone is asked. An application is trusted only when the user approved with `remember` for that client; no operator list.
- The reduced cap, the hourly limit and the pause are modelled per client, not per origin (the pending limit is per origin). `MOCK_MAX_PENDING` and `MOCK_MAX_PER_HOUR` (options `maxPendingPerClientOrigin`, `maxRequestsPerHour`) raise the limits for a long contract run. Receipts must be protocol 1.1; a 1.0 receipt is refused. The enrollment proof, the pairing code and activation by another device (T-242) are not modelled; `POST /devices` takes an optional `presence_kid` and `presence_public_key`.
- State is in memory. Nothing is sent to FCM, APNs or a device channel: each new request queues one message per active device with a push token (`GET /mock/push-outbox`), and clients can also poll `GET /devices/requests`.

- Platform attestation (T-234): `POST /devices/challenge` (login or device token) issues a single-use nonce; `POST /devices` accepts `attestation_challenge` and `platform_attestation` together, and `PUT /devices/attestation` refreshes it. The mock checks the shape, binds the nonce to the user, spends it once and expires it after 300 s; it cannot verify a token with Google or Apple. `attestation_refresh_after` comes back when one was accepted.
