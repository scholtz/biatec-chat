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
| `POST /register`, `POST /consent/links`, `GET /consent/links/{id}`, `POST /consent/links/{id}/receipt`, `DELETE /consent/links/subject/{subject}`, `GET /consent/links/subject/{subject}/devices`, `GET /consent/requests/{id}/receipt`, `POST /mock/serve` | Relying parties (protocol 1.2, T-246): `private_key_jwt` registration, linking on the phone, payment, login and action requests, the receipt for a pinned key. `POST /connect/ciba` and `POST /token` also take a relying party. See "Relying parties" below |
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

## Relying parties (protocol 1.2, T-246)

What a bank needs to use Biatec Authenticator as its confirmation app (spec chapter 14; Biatec OIDC issue 38). Everything is validated against the 1.2 schemas (`link-created`, `link-request`, `link-status`, `consent-request`, `token-response`, the three `authorization-details-*`). Try it end to end with the demo below.

**Client authentication.** A relying party has no secret. Every call it makes carries an RFC 7523 assertion (a compact JWS, `ES256` or `PS256`, `iss` = `sub` = `client_id`, `aud` the issuer or `<issuer>/token`, `exp` at most 300 seconds ahead, a single-use `jti`, `kid` when the key set has more than one key of the kind). A request with a body carries it in the form or JSON members `client_assertion_type` (`urn:ietf:params:oauth:client-assertion-type:jwt-bearer`) and `client_assertion`; a request without a body (the `GET`s and the `DELETE`) carries it as `Authorization: Client-Assertion <jwt>` (the mock's choice: issue 38 does not say). Any failure (unknown client, bad signature, `none`, HS256, wrong audience, expired, too far ahead, `iss` or `sub` differing, a replayed `jti`, `Authorization: Basic` with the client's id) is `401 invalid_client`.

| Call | Request | Answer and errors |
| --- | --- | --- |
| `POST /register` | JSON (RFC 7591): `token_endpoint_auth_method: "private_key_jwt"`, `client_uri` (https URL, host name, no IP address, no credentials), exactly one of `jwks` (1 to 4 keys, EC P-256 or RSA of 2048 bits or more, no private member, `use` `sig`) and `jwks_uri` (https; the mock fetches it through its stand-in fetcher, `serve()` or `POST /mock/serve`, and caches it for one hour). Optional `grant_types` (only `urn:openid:params:grant-type:ciba`), `redirect_uris` (only `[]`), `scope` (only `openid`) | `201 { client_id, client_id_issued_at, token_endpoint_auth_method, grant_types, client_uri, jwks or jwks_uri }`, never a secret. `400 invalid_client_metadata` (any other method, grant, scope, key or `client_uri`), `400 invalid_redirect_uri`, `429 rate_limited` (per source address, `maxRegistrationsPerHour`, default 100, `Retry-After`) |
| `POST /consent/links` | `{ "user_label"?: string }` (at most 40 characters, text rules of spec chapter 15: refused, never stripped); unknown members are refused | `201` `link-created`: `{ link_id, link_uri, binding_code, expires_in: 300, interval: 2 }`, `link_uri` = `biatec-authenticator://link?iss=<percent-encoded issuer>&id=<link_id>&s=<link_secret>` (32 random bytes, stored hashed). `400 invalid_request`, `403 unauthorized_client` (a seeded wallet client), `429 rate_limited` (50 open links per party) |
| `GET /consent/links/{id}` (device) | `Authorization: Device <token>` of an ACTIVE device and `X-Link-Secret: <s>` | `200` `link-request`: `{ link_id, client: { fingerprint, domain_proven, domain? }, user_label?, nonce, expires_at }`; the fingerprint and domain shown are remembered for the statement. `404 not_found` (unknown id, wrong or missing secret), `403 access_denied` (device not active yet), `409 conflict` (decided already), `410 expired_token` |
| `POST /consent/links/{id}/receipt` (device) | `Authorization: Device <token>` and `X-Link-Secret: <s>` (required, as for the fetch: missing or wrong is `404`); body `{ signed, signature }` (`link-signed`; ES256 over RFC 8785 of `signed` by the APPROVAL key for `approve`, either key for `deny`) | `200 { status: "linked" or "denied" }`. Checked: the `link-signed` schema; the key is one of the calling device's; `link_id`, `client_fingerprint`, `domain` (the proven domain shown, or `null`) and `nonce` equal what was shown (`400 receipt_mismatch`: **a wrong domain is refused**); `verifyLink` (signature, approval key for approve, `issued_at` within 300 s; `400 invalid_receipt`); the code (`400 wrong_code` with `attempts_remaining`; the third wrong code is `403 access_denied` with `reason: code_failed` and closes the link); single use (`409 conflict`); `410 expired_token`. On approve: the user knows the client (linked in the settings, shown on the phone, removable there), the pairwise `subject` is recorded, the other devices get `settings_changed` |
| `GET /consent/links/{id}` (party) | client authentication; only the creating party (another gets `404`) | `200` `link-status`: `pending`; `linked` with `subject`, `devices` and `approval { signed, signature, attestation }`; `denied` with `approval`; `expired` (also what a link closed by three wrong codes shows). Polling a `pending` link faster than `interval` is `400 slow_down { interval }` (plus 5 s each time) |
| `DELETE /consent/links/subject/{subject}` | client authentication | `200 { unlinked: true }`; the party's open requests for that user are closed (denied, no receipt). `404` for a subject that is not linked to this party (also one that never existed) |
| `GET /consent/links/subject/{subject}/devices` | client authentication | `200 { devices: [ { kid, key_use: "approve" or "presence", jwk } ] }` (`link-status#/$defs/devices`): the current approval and presence keys of the account's ACTIVE devices (a phone added later appears here, spec 14.6). `404` as above |
| `POST /connect/ciba` | client authentication; `scope: "openid"`, `login_hint` = the linked `subject`, `authorization_details` = exactly one `biatec_payment`, `biatec_login` or `biatec_action` object (a JSON array; in a form a JSON text), optional `origin` (canonical origin; the `Origin` header is used when it is absent), optional `requested_expiry`. No `binding_message` | `200` `backchannel-authentication-response` (`auth_req_id`, `expires_in`, `interval`, `binding_code`). Order of checks: scope; `login_hint` (`400 unknown_user_id`: one answer for an unlinked, unlinked-again, other party's or never existing subject, whatever the content); the object (`400 invalid_authorization_details` naming the first rule broken: schema, unknown member, an `origin` inside the object, a text rule of chapter 15 for every string, the IBAN's mod-97 check digits, amount, date; nothing is stripped); `origin`; then `403 consent_unavailable` (`reason: no_active_device`), `403 client_paused`, `403 unproven_client_blocked`, `429 too_many_pending_requests` (5 open per user and party), `429 rate_limited` (60 a minute per party, `Retry-After`). `403 unauthorized_client` for `sign`, `manage-limits` or any wallet scope, and for a seeded wallet client that sends `openid` |
| `POST /token` | client authentication, form `grant_type=urn:openid:params:grant-type:ciba&auth_req_id=...` | As above: `authorization_pending`, `slow_down`, `access_denied` (a deny or `reason: code_failed`; the receipt is then fetched), `expired_token`, `invalid_grant` (single use, another party's id). `200` `token-response`: scope `openid` only, `consent_receipt` = the 1.2 receipt. The token is refused (`401 invalid_token`) by every endpoint, the signing endpoint, `GET /consent/settings` and the device endpoints included. Any other grant is `400 unauthorized_client` |
| `GET /consent/receipts/{auth_req_id}` and `GET /consent/requests/{auth_req_id}/receipt` | client authentication; only the party the request belongs to (`404` otherwise, or before the decision) | The same `consent-receipt` as the token response. **Issue 38 names the second path; the first is the existing one and both are served.** A deny and a `code_failed` receipt are served here too |

**The phone side of a request** is the existing one (`GET /consent/requests/{id}`, `POST /consent/requests/{id}/receipt`, `Authorization: Device ...`) with these differences: the receipt is version `"1.2"` (`details_hash` in `signed`, `binding_message` empty, no `remember`; a 1.1 receipt for a relying party's request, or a 1.2 one for a wallet request, is `400`); `signed.sub` is the pairwise subject of the party (the request carries it); `device.key_use` must name the use of the key; `approve` needs the approval key (a presence-key approve is `400 invalid_receipt`, a deny accepts either); and the receipt must restate the stored request, `details_hash` = `bound_hash` included (`400 receipt_mismatch`). The device's own attestation names the account, so the mock **replaces it** with one minted for the pairwise subject when it stores the receipt (spec section 8: "minted when the receipt is stored"); `verifyReceipt` and `verifyApproval` (attestation mode, `deviceActive`) then run on the stored receipt. A request of a relying party always goes to the phone: the threshold, trusted applications and first-use rules do not apply, and approving adds no trusted applications.

**Domain proof.** A relying party is proven only while `https://<host of client_uri>/.well-known/biatec-client.json` lists its `client_id` in `client_ids` (at most 50 entries); the mock reads the file on every use (the real server re-checks daily). The mock never goes out to the network: the file (and a `jwks_uri`) is served from its own table, filled by `serve(url, json)` on the object `createMockOidc` returns, or by the helper `POST /mock/serve { "url": "https://<host>/.well-known/biatec-client.json", "json": { "client_ids": ["<client_id>"] } }` (`json: null` removes it; test helpers only). Unproven: the link request has `domain_proven: false` and no `domain`, and the statement signs `domain: null`.

**Test helpers and options.** `POST /mock/serve` (above), `GET /mock/devices/{kid}` (the seeded devices' token, keys and attestation), `POST /mock/users/{sub}/clients/{client_id}/pause` (works for a relying party's `client_id`) and `POST /mock/devices/{kid}/activate` are served only with `MOCK_TEST_HELPERS=1` (always in embedded tests). `createMockOidc` options: `maxRegistrationsPerHour`, `maxRelyingPartyRequestsPerMinute`, `maxRelyingPartyPendingPerUser`, `fetchJson`; `src/server.ts` reads `MOCK_MAX_REGISTRATIONS_PER_HOUR`, `MOCK_RP_MAX_PER_MINUTE` and `MOCK_RP_MAX_PENDING`. The returned object also has `serve` and `pairwiseSubject(clientId, accountSub)`. `src/test-device.ts` (a typed entry point for `../scripts/rp-client.mjs`, plain JavaScript) holds what the tests, the conformance kit and the demo use: `generateRpKey`, `clientAssertion`, `registerRelyingParty`, `rpCall` (a call with a fresh assertion), `proveDomain`, `testDevice`, `approveLink` and `approveRequest` (the scripted phone: it builds the `biatec_link` statement and the 1.2 receipt, computes `details_hash` itself and signs with the test-only device keys of `../vectors/keys.json`).

**What the mock does not model**: the hardened fetcher (no network), `jwks_uri` beyond one cached fetch, the daily re-check of the domain, the email and the `service_linked` security event, the rate limits beyond the ones above, and nothing else of the link flow is left out.

### The demo

`node ../scripts/rp-demo.mjs --server <url>` (or `RP_DEMO_SERVER`) registers with a key generated in memory, prints the QR payload and the code, waits for the link (and verifies the user's statement), sends a 12.50 EUR payment request, polls, fetches the receipt and verifies it with `verifyReceiptPinned` and the pinned keys only (no JWKS, no attestation), printing each step. Nothing is written to disk and no assertion or token is printed. Against a mock started with `MOCK_TEST_HELPERS=1` the phone is scripted; otherwise (or with `--manual`) it waits (`--timeout`, default 300 s) for a real phone. The domain it expects in the user's statement is the host it registered when it published the well-known file (always when scripted, with `--domain-published` otherwise) and `null` when not, never what the statement says. The script and its helpers (`../scripts/rp-client.mjs`) are plain JavaScript and run on Node 20 or newer. From this folder: `PORT=8095 MOCK_TEST_HELPERS=1 node src/server.ts & pid=$!; node ../scripts/rp-demo.mjs --server http://127.0.0.1:8095; kill $pid`.

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
| `clients.{client_id}.unlink: true` | the only member for that client; forgets the link (and the first day with the client: the next request is first use again; a client that is not linked is `400 invalid_request`), label, trusted applications, own threshold and "always ask" (a running pause stays) | tightening |

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
