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
| `POST /devices` | Enrollment with a `login-*` token (scope `enroll-device`, fresh `auth_time`: `login-alice-stale` is refused with `401 insufficient_user_authentication` and `max_age`); inactive 24 h unless `activation: { kind: "recovery_code", code }` is right (`403 invalid_recovery_code`; 5 wrong codes lock the account for 15 minutes: `429`); `approved_by_kid` is `400`. Optional `name` |
| `GET /authorize`, `POST /token` (`grant_type=authorization_code`) | The authenticator's in-app login (T-242): PKCE S256, public client `biatec-authenticator`, redirect `https://oidc.biatec.io/app/authenticator/callback` or `io.biatec.authenticator:/oauth2redirect`; the mock logs in at once as `login_hint` (default `alice@example.com`) and answers a 300 second login token with scope `openid enroll-device` and `auth_time`, no refresh token |
| `POST /devices/{kid}/activate` | `Authorization: Device <token>` of an active device of the same account; body `{ approval }`: the `biatec_device_approval` signed with the APPROVAL key, checked with `verifyDeviceSigned` (the pairing code, both thumbprints, the enrollment nonce, one-time statement nonce); a presence-key signature, a wrong code and another account's approver are refused (`403`, `404`) |
| `POST /devices/recovery-code`, `POST /devices/recovery-code/ack` | `Authorization: Device <token>`; the recovery code is issued only once the device is active (403 while inactive), re-issued until acknowledged, then 409 (rule 7) |
| `GET /devices/requests`, `GET /consent/requests/{id}`, `POST /consent/requests/{id}/receipt` | Authenticator side, `Authorization: Device <token>`; the request never contains the binding code |
| `GET /.well-known/jwks.json`, `/.well-known/openid-configuration` | Discovery |
| `PUT /devices/push` | `Authorization: Device <token>`; body `{ "provider": "apns", "token": "<hex>" }` registers or replaces the device's Apple push token. `fcm` is `400 unsupported_push_provider` (no Firebase, T-244) |
| `POST /devices/hub-ticket` | `Authorization: Device <token>` (and the device proof where required); answers `{ "ticket": "...", "expires_in": 30 }`. The ticket is single use and lives 30 seconds |
| `WS /hubs/device?access_token=<ticket>` | The device channel, SignalR JSON hub protocol, server to client only (`src/device-hub.ts`). A used, expired or unknown ticket is refused with HTTP 401 at the upgrade. See "Device channel" below |
| `GET /devices`, `POST /devices/{kid}/cancel` | Device list (with `name`, and `pairing` for a pending device that has two keys); cancelling a pending enrollment (T-084) |
| `GET /devices/settings`, `PUT /devices/settings` | The user's own consent settings, set on the phone (issue 30). Exact shapes, the classifier and the 403 body: see "Settings (issue 30)" below |
| `POST /mock/hub/close` | Test helper, body `{ "kid": "...", "allow_reconnect": false }`: sends the close message to the device's channel connections and closes them |
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
- Users: `alice@example.com` (active device `alice-device`, 1000 EUR threshold), `carol@example.com` (active device, always asks for `biatec-chat`), `bob@example.com` (no device), `dave@example.com` (active device, paused `biatec-chat`: `client_paused`), `erin@example.com` (no device and no earlier signature: the reduced cap, `daily_cap_exceeded`), `frank@example.com` (active device, threshold 5000 EUR), `gina@example.com` (active device, blocks clients without a proven domain), `hank@example.com` (active device, `biatec-chat` linked; nothing else uses it: the conformance settings cases change its settings). A device has an approval key (`<name>-device`, the vector `device` key) and a presence key (`<name>-device-presence`, the vector `presence` key), each attested for its own use.
- Bearer tokens: `alice-chat-sign`, `alice-chat-limits`, `alice-bank-sign`, `alice-new-sign`, `alice-unverified-sign`, `carol-chat-sign`, `bob-chat-sign`, `dave-chat-sign`, `erin-chat-sign`, `frank-chat-sign`, `gina-chat-sign`, `gina-unverified-sign`, `hank-chat-sign`, `alice-chat-nosign`; login tokens `login-alice`, `login-bob`, `login-carol` (scope `openid enroll-device`, authenticated just now) and `login-alice-stale` (authenticated an hour ago: refused for enrollment). Seeded device tokens: `Device device-token-<name>`.

## Mocked simplifications

- Groups are real unsigned Algorand transactions (`algosdk` 3.9.0, base64 msgpack). `src/build.ts` builds them for tests (`buildGroup`, `usdcTransfer`, `account`). "Signing" returns the transactions with an all-zero signature. Bitcoin cannot be decoded: a Bitcoin request is `403 consent_unsupported_network`. `MOCK_JSON_GROUPS=1` (option `jsonGroups`) switches to base64(JSON) descriptors (`src/group.ts`, `Tx`); only the e2e demo harness uses it.
- Valuation is a static price table in `src/group.ts` (ALGO 0.20, USDC 0.9225 EUR, on mainnet and testnet alike); any other asset is unpriced, so the value is unknown and the phone is asked. An application is trusted only when the user approved with `remember` for that client; no operator list.
- The reduced cap, the hourly limit and the pause are modelled per client, not per origin (the pending limit is per origin). `MOCK_MAX_PENDING` and `MOCK_MAX_PER_HOUR` (options `maxPendingPerClientOrigin`, `maxRequestsPerHour`) raise the limits for a long contract run. Receipts must be protocol 1.1; a 1.0 receipt is refused. Activation by another device, the pairing code, the in-app login and the recovery code at enrollment are modelled (T-242, see the table above): the new device's `pairing` data in `GET /devices` carries the thumbprints, the enrollment nonce and the code; a `device_enrolled` entry goes to the push outbox (`data: { event: "device_enrolled", kid }`) and the hub when a device is enrolled and when it is activated. `POST /devices` takes an optional `presence_kid` and `presence_public_key`; with `MOCK_TWO_KEYS=1` (option `twoKeys`; use it for the authenticator contract tests) it REQUIRES them plus `approval_proof` (the enrollment proof signed by the new approval key, carrying the nonce of a `POST /devices/challenge` answer) and `presence_proof` (same payload, signed by the presence key), verified with `verifyEnrollmentProof`, and the device proof (`MOCK_DEVICE_PROOF=1`) must be signed by the presence key (a proof signed by the approval key is `401`). A challenge that is unknown, used or expired is `400 invalid_challenge` and a proof that does not verify is `400 invalid_proof` (the authenticator retries once, with a fresh challenge, on exactly these codes). An approve receipt signed by the presence key is refused in every mode.
- State is in memory. Nothing is sent to APNs: each new request queues one message per active device with a push token (`GET /mock/push-outbox`), and clients can also poll `GET /devices/requests`.

- Platform attestation (T-234): `POST /devices/challenge` (login or device token) issues a single-use nonce; `POST /devices` accepts `attestation_challenge` and `platform_attestation` together, and `PUT /devices/attestation` refreshes it. The mock checks the shape, binds the nonce to the user, spends it once and expires it after 300 s; it cannot verify a token with Google or Apple. `attestation_refresh_after` comes back when one was accepted.

## Settings (issue 30, T-245): the source of truth for the authenticator

All trust is the user's, set on the phone; the mock trusts no client and no application id. Every call here: `Authorization: Device <token>` (plus the device proof when `MOCK_DEVICE_PROOF=1`). The device must be **active** (an inactive one gets `403 access_denied`, tightening included; `GET` is open to it). Env switch: `MOCK_MAX_THRESHOLD_EUR=<n>` (option `maxThresholdEur`) sets an operator ceiling for the threshold; unset there is none except the technical 1000000000 EUR.

**`GET /devices/settings`** answers 200:

```json
{
  "sub": "pw_alice",
  "biometric_required": true,
  "threshold_eur": "1000.00",
  "default_threshold_eur": "1000.00",
  "max_threshold_eur": "5000.00",
  "block_unproven_clients": false,
  "ask_on_test_networks": true,
  "clients": [
    {
      "client_id": "biatec-chat", "label": null, "fingerprint": "a1b2c3d4e5f60718", "domain_proven": true, "domain": "chat.biatec.io",
      "linked": true, "linked_at": 1792425600, "always_ask": false, "per_client_threshold_eur": null,
      "paused_until": null, "pauses": [],
      "trusted_applications": [ { "network": "algorand-testnet", "app_id": 777 } ]
    }
  ]
}
```

- Amounts are decimal strings with two decimals. `max_threshold_eur` is present only when the operator set a ceiling. `block_unverified_clients` is gone (renamed `block_unproven_clients`); the members `verified` and a server-asserted name are gone too: a client is told apart by its `fingerprint` (16 hex characters) and, when `domain_proven`, its `domain` (absent otherwise). `label` is the user's own text or `null`.
- `clients` lists the clients that are linked or hold any setting of the user's (a label, a trusted application, "always ask", a threshold of their own, a running pause). `linked_at` is epoch seconds (`null` when not linked; a seeded client counts from the start of the mock). `paused_until` is epoch seconds while the 24 hour pause runs, else `null`; `pauses` lists `{ "origin", "paused_until" }` (the mock models one pause per client; its origin is the client's own `https://<domain>`). `per_client_threshold_eur` `null` follows `threshold_eur`.

**`PUT /devices/settings`**, body `{ "change": { ... }, "consent": { ... }? }`. Answers 200 `{ "applied": true, "loosened": <bool>, "settings": <the GET body> }`, and sends `settings_changed` on the device channel.

`change` has at least one member, all optional; unknown members are `400 invalid_request`:

| Member | Type and rule | Direction |
| --- | --- | --- |
| `threshold_eur` | decimal string, at most two decimals, `0` (always ask) up to the ceiling | raising is loosening |
| `biometric_required`, `block_unproven_clients`, `ask_on_test_networks` | boolean | turning off is loosening |
| `clients.{client_id}.always_ask` | boolean | off is loosening |
| `clients.{client_id}.per_client_threshold_eur` | amount, or `null` (follow `threshold_eur`) | raising it above the effective one (or `null` when that raises it) is loosening |
| `clients.{client_id}.label` | 1 to 40 characters, `isDisplaySafe` (NFC, no control, bidirectional, zero-width or invisible characters, no padding); only for a linked client | tightening |
| `clients.{client_id}.trusted_applications_add` | 1 to 16 unique `{ "network": "algorand-mainnet" or "algorand-testnet", "app_id": <integer >= 1> }`; only for a linked client | loosening |
| `clients.{client_id}.trusted_applications_remove` | same list shape; not the same application as `_add` | tightening |
| `clients.{client_id}.paused: false` with `origin` | the canonical origin of a running pause (only `false`: a pause is set by denials) | lifting is loosening |
| `clients.{client_id}.unlink: true` | the only member for that client; forgets the link, label, trusted applications, own threshold and "always ask" (a running pause stays) | tightening |

Loosening versus tightening is decided here, by the mock, never by the app; a change that mixes both is loosening as a whole. Send the change; if it is a loosening and carries no valid `consent`, nothing changes and the answer is

```
403 { "error": "insufficient_user_authentication", "error_description": "<why>", "acr_values": "biatec:consent", "reason": "consent_required" }
```

The app then signs the same `change` and resends it. `consent` is the device-signed envelope of spec 8.2 (`device-signed.schema.json`), checked with `verifyDeviceSigned`:

```json
{ "signed": { "type": "biatec_settings_change", "sub": "pw_alice", "kid": "alice-device", "nonce": "<16 to 128 characters, unused>", "issued_at": 1792425600, "change": { "...": "equal to change" } },
  "alg": "ES256", "signature": "<base64url r||s over JCS(signed), by the APPROVAL key>",
  "device": { "kid": "alice-device", "key_use": "approve", "public_key": { "kty": "EC", "crv": "P-256", "x": "...", "y": "..." }, "attestation": "<the approval key's attestation JWS>" } }
```

It must be signed by the approval key of the calling (active) device (`device.kid` is the caller's; a presence-key signature is refused), `signed.sub` is the caller's account, `signed.change` equals `change` (canonically), `issued_at` is within 300 seconds, the nonce is new for that device (spent on success only), and the change must be well formed (a display-safe label and so on). Anything else is the same `403` and nothing changes. A `400` (bad shape, amount over the ceiling) is answered before the signature is looked at.

The `400` for an amount over the ceiling carries the text the app shows: `{ "error": "invalid_request", "error_description": "threshold_eur must not be more than 5000.00 EUR (the limit of this service)" }` (with no operator ceiling the text ends `... 1000000000.00 EUR` without the bracket). The same ceiling applies to `per_client_threshold_eur`.

**Linking and remembering.** A client is linked by the user's first approved request for it; before that `first_use` (in `GET /consent/settings`) is true, the consent request's `client.linked` is false, and every request asks the phone whatever the amount. An `approve` receipt may carry the signed member `remember: { "applications": [ids] }` (protocol 1.1): each id must be an application of that request that the card offered as untrusted (`trusted: false`), 1 to 16 unique; otherwise `400 invalid_receipt` and the request stays pending (a `deny` receipt with `remember` is refused too). The ids become the trusted applications of that client on the request's network; the signing policy then treats them as known (the value counts, nothing is asked below the user's threshold). `unlink` makes the next request first use again.

**Policy.** The signing policy uses the user's `threshold_eur` (or the client's own), `always_ask`, `ask_on_test_networks` (off: a test network no longer asks because of the value, every other reason still applies), `block_unproven_clients` and the trusted applications. `POST /mock/users/{sub}/clients/{client_id}/pause` (test helper) starts a 24 hour pause.

## Device channel (T-244)

`POST /devices/hub-ticket`, then connect `ws://<host>/hubs/device?access_token=<ticket>`. Every message is JSON followed by the record separator `0x1e`; a frame may hold several.

1. Client sends `{"protocol":"json","version":1}` + `0x1e`; the server answers `{}` + `0x1e` (anything else: `{"error":"..."}` and close).
2. Server events are invocations `{"type":1,"target":"<event>","arguments":[{...}]}`, payloads are ids only: `consent_request {auth_req_id}` (a request for the user opens), `consent_closed {auth_req_id}` (approved, denied, code attempts used up, or expired; expiry is noticed within a second of the mock clock passing it), `device_enrolled {kid}` (a device of the user is enrolled or activated), `settings_changed {}`. Events go to every connected device of that user. The client never sends invocations.
3. Ping `{"type":6}`: the server answers a client ping with a ping and sends one every `MOCK_HUB_PING_SECONDS` (default 15).
4. Close `{"type":7,"allowReconnect":true}` after `MOCK_HUB_LIFETIME_SECONDS` (default 1800; options `hubLifetimeSeconds`, `hubPingSeconds`), or with `allowReconnect: false` when the device is cancelled or `POST /mock/hub/close` asks for it.

The push outbox (`GET /mock/push-outbox`) records the same request-opened events as before.
