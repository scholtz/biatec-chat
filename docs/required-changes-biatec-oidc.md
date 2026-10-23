# Required changes to Biatec OIDC: handoff spec and fallback matrix

This is the handoff for the Biatec OIDC owners and the matrix biatec-chat agents implement against. It covers every row of the
"Required Changes To Biatec OIDC" table in `PRODUCT-DESCRIPTION.md` (the specification, which wins on any conflict). We work only
in this repository, so for each row this file states the API shape the product expects and the fallback this repo implements now.
Examples are illustrative: names the spec fixes are normative (`insufficient_user_authentication`, `biatec:consent`,
`auth_req_id`, `consent_receipt`, `requireConsent`, `valuedEur`, `maxConsentSeconds`, `authorization_details`, the
`biatec_transaction` and `biatec_limits_change` types); other names are proposals for the OIDC owners to confirm.

Today Biatec OIDC provides: authorization code with PKCE, dynamic client registration (public clients only), resource indicators,
the `sign` / `manage-limits` / `rekey` scopes, `POST /wallet/{network}/{address}/sign` with spending limits, and transaction
decoding for limit enforcement.

- **Needed:** confidential-client dynamic registration for tenants that pass domain verification (today `POST /register` issues public clients only).
- **Fallback in use:** an operator adds each tenant's client (`Tenant.OidcClientId`, authorization-code grant, S256 PKCE, `resource` indicator
  `https://mcp.biatec.io/tools`, redirect URI `Tenant.RedirectUri`) to Biatec OIDC's static client list. The client secret is supplied to the
  backend as configuration `Secrets:<path of Tenant.ClientSecretRef>` (for example `Secrets__biatec-chat__oidc-client-secret`), never in the repo.
- **Status:** automated tests of `/auth/login` run only against the in-repo mock OIDC (`backend/Biatec.Chat.Tests/Auth/MockOidc.cs`). Stage uses
  the client named by the `Stage` environment's `OIDC_CLIENT_ID` at stage.oidc.biatec.io, and the demo site lists sign-in on stage as working,
  subject to its post-deploy checks ([demo-site.md](demo-site.md)). A production registration on oidc.biatec.io is not confirmed in this repository.

## Tenant onboarding registration (T-045)

- **Needed:** the same confidential-client dynamic registration (RFC 7591) as above, called by `POST /onboarding/tenants` once the DNS TXT proofs pass.
- **Fallback in use:** `IOidcClientRegistrar` defaults to `NoDynamicRegistration`, so the endpoint answers `202 pending-operator-approval` and stores nothing.
  An operator adds the static client, puts the secret in the secret store, then calls `POST /onboarding/operator/tenants` (bearer: a named `Admin:Operators` key, or the deprecated `Onboarding:OperatorKey`; the approval is recorded in the signed admin evidence chain, see docs/admin-api.md)
  with the `oidcClientId` and a `secret://` reference; the DNS proofs are re-checked before the tenant row is written.
- **Also missing:** a real `ISecretStore` (AWS Secrets Manager or Kubernetes secrets); only the interface and a throwing default exist.
- **Challenge validity:** the TXT value embeds a 7-day epoch bucket and the current and previous bucket are accepted (a record works for 7 to 14 days). It is not bound to the requester; anyone who reads the public TXT can submit the identical configuration, which yields exactly the tenant the zone owner asked for.
- **Abuse limits:** `/onboarding/*` is rate limited per remote IP (`Onboarding:RateLimitPermits`, default 20 per `Onboarding:RateLimitWindowSeconds`, default 60) and bodies above `Onboarding:MaxBodyBytes` (32 KiB) get 413. Behind a proxy, forwarded-headers handling is needed so the client IP is partitioned, not the proxy. A per-target-host concurrency limit is deferred.
- **Secret cleanup:** `ISecretStore.DeleteAsync` removes the stored secret best-effort when the tenant row write fails for a non-conflict reason.

## Fallback matrix

"Mock" means the in-repo mock OIDC used by tests (`backend/Biatec.Chat.Tests/Auth/MockOidc.cs` and its successors) implements the
shape below so the backend is tested against it. "None" means the spec states there is no fallback, so an agent builds against the mock and records the dependency rather than inventing one: the feature runs only
against the mock and cannot be enabled against real Biatec OIDC until the row ships.

| # | Addition | Fallback implemented in this repo now |
| --- | --- | --- |
| 1 | CIBA backchannel authentication endpoint and token grant | None. Consent flows run only against the mock; above-threshold signing is unavailable against real OIDC until it ships |
| 2 | `authorization_details` types `biatec_transaction`, `biatec_limits_change` | None. Mock builds them from the group |
| 3 | Step-up challenge on sign and `PUT /wallet/limits`, `requireConsent` | None. Mock returns the challenge; on the wallet signing and limits endpoints the backend treats a 401 `insufficient_user_authentication` with an `auth_req_id` as the only consent trigger, and retries the same request after the poll. `requireConsent` (set by the soft cap and safe caps, T-025) is sent only when `Caps:OidcHonoursRequireConsent` is true (mock only; default false): against real Biatec OIDC an action a cap would send to the phone is refused instead, so the cap is a hard stop for the rest of the window. Whenever `requireConsent` was sent and OIDC signs without a challenge, the backend refuses to submit (`consent_not_enforced`) |
| 4 | Device enrollment, attestation, receipts, `GET /consent/receipts/{auth_req_id}` | None. Authenticator and backend develop against the mock. Rules the mock models and the real server must keep (BCHAT-024): a receipt is returned only to a bearer whose subject owns the request (a client credential may fetch the receipts of its own requests); `slow_down` to a client that polls faster than `interval` and +5 s on the interval after each; a bounded number of open requests per user (429); a device that is not active yet (rule 7) can neither change settings, tightening included, nor cancel enrollments |
| 5 | Algo Safe application-call valuation | Unknown application calls are treated as above threshold (always ask) |
| 6 | Biatec DEX pool call decoding | Treated as an unknown application call (always ask); card shows decoded transfers and the application ID only |
| 7 | Test-network valuation from a static price table | Demo identities set to "always ask" for the Biatec Chat client with no spending limits |
| 8 | Group-hash binding in the signing endpoint | None. The backend also recomputes the group ID before calling, as defence in depth, not a substitute |
| 9 | Confidential-client registration for tenants | Operator adds the tenant client to the static client list (section 9) |
| 10 | Reduced per-client daily cap without an active device | Backend enforces the 100 EUR per-client daily cap and blocks above-threshold signing for users with no active device (T-061, `Caps:NoDevice`). "No active device" is known only through the backend's own enrollment record or row 20; where neither exists, the backend cannot tell and relies on OIDC's step-up challenge. Set `Caps:NoDevice:Enabled` to false once Biatec OIDC enforces the rule itself |
| 11 | `valuedEur` in every signing response | Backend counts its own predicted value in the soft cap; only as good as its price feed |
| 12 | OpenID Federation trust-chain evaluation for client display names | Biatec's own clients pinned as verified by configuration; all others shown unverified |
| 13 | Forced fresh consent on scope elevation | Not needed: every authorization shows the consent screen |
| 14 | RFC 7009 revocation of exactly the presented refresh token | Destroy the vault entry and let the token age out; deployment security notes say so. Residual risk: a refresh token stolen earlier stays valid at the IdP until expiry (see row 18) |
| 15 | Pairwise subject identifiers per tenant client | Public `sub` used; deletion cannot unlink retained receipts (documented) |
| 16 | `maxConsentSeconds` on the signing request | Rely on the 600-second Confirm gate and rebuild more often |
| 17 | Origin-aware consent requests | Pending slots, rate limits and pauses apply per client for all Biatec sites together; the backend treats HTTP 429 as "try later" in the widget |
| 18 | Per-client refresh token lifetime (8 h) with absolute-expiry-preserving rotation | Keep 30-day tokens, destroy the vault entry at logout (no IdP revocation, row 14); residual exposure at the IdP is 30 days |
| 19 | Consent-gated loosening of consent settings | Loosening only from an enrolled device's own screen; chat hides raising limits (`limits.update` is lowering-only) |
| 20 | `GET /consent/settings` | Card says "may need approval on your phone" without predicting |
| 21 | Device list and cancelling a pending enrollment from a device (`GET /devices`, `POST /devices/{kid}/cancel`) | None. The authenticator's device screen runs only against the mock; `CONSENT_SETTINGS_API` is off for any other Biatec OIDC and the screen then says "not available yet" (T-084) |
| 22 | Device-side consent settings (`GET` / `PUT /devices/settings`) with device-signed loosening | None. The security and trusted-clients screens run only against the mock, behind the same `CONSENT_SETTINGS_API` flag (T-084) |
| 23 | Operator identity claim (`biatec_operator` group) for the admin API | Named operator bearer keys: `Admin:Operators:<name>:KeyHash` holds only the SHA-256 of each operator's key, and every admin change records the operator name in the signed admin evidence chain (T-097, docs/admin-api.md). The shared `Onboarding:OperatorKey` is still accepted as the deprecated operator `legacy` |

**Mock status.** Today the mock covers only the authorization-code flow used by `/auth/login`. The CIBA, step-up, receipt and
settings shapes below are the contract the mock must grow to, as the consent tasks (backend and authenticator) are implemented;
statements like "the mock returns this" describe that target, not what exists now.

Conventions in the examples: `{network}` is a network name such as `algorand-testnet`; bearer tokens are held only by the backend
(the browser never sees them); signing always goes from the backend straight to `POST /wallet/{network}/{address}/sign`.

## 1. CIBA backchannel authentication endpoint and token grant

- **Purpose:** opens and completes consent requests. **Fallback:** none.
- **Entry points:** step-up (OIDC opens the request while answering a wallet call, row 3) and direct (the client opens it first).
  Both produce the same request object: `authorization_details`, binding code, optional `binding_message`, client identity,
  `requested_expiry`, bound hash.
- **Direct-entry request** (client authenticated as at the token endpoint):

```http
POST /connect/ciba HTTP/1.1
Content-Type: application/x-www-form-urlencoded
Authorization: Basic <client credentials>

scope=sign&resource=https%3A%2F%2Fmcp.biatec.io%2Ftools
&login_hint=<the client's own sub for this user (pairwise once row 15 ships)>&requested_expiry=300
&binding_message=Add%20liquidity%20to%20EURS%2FUSDC
&biatec_network=algorand-testnet&biatec_address=ADDR...
&biatec_origin=https%3A%2F%2Fdex.biatec.io
&biatec_group=<base64 unsigned group>
```

```json
{ "auth_req_id": "ar_8f2c...", "expires_in": 300, "interval": 2, "binding_code": "4821" }
```

- **Token grant** (poll mode only; ping and push delivery are out of scope and need a separately specified, authenticated callback):

```http
POST /connect/token
Authorization: Basic <client credentials>

grant_type=urn:openid:params:grant-type:ciba&auth_req_id=ar_8f2c...&resource=https%3A%2F%2Fmcp.biatec.io%2Ftools
```

Pending: `400 {"error":"authorization_pending"}`; too fast: `400 {"error":"slow_down"}`; denied: `400 {"error":"access_denied"}`;
expired: `400 {"error":"expired_token"}`. Success:

```json
{
  "access_token": "<jwt, acr=biatec:consent, same scope and audience as challenged, bound to the group hash>",
  "token_type": "Bearer",
  "expires_in": 300,
  "scope": "sign",
  "consent_receipt": {
    "authorization_details": [ { "type": "biatec_transaction", "...": "see section 2" } ],
    "auth_req_id": "ar_8f2c...",
    "client": "<entity id, or client_id when the client has none>",
    "binding_code": "4821",
    "decision": "approve",
    "expiry": "2026-10-20T09:05:00Z",
    "sub": "<sub>",
    "binding_message": "",
    "signature": { "alg": "ES256", "kid": "<device key id>", "sig": "<base64url>", "attestation": "<ref>" }
  }
}
```

The signature covers the RFC 8785 canonical JSON of `{authorization_details, auth_req_id, client entity ID, binding code, decision,
expiry, sub, binding_message}`. A denial returns `access_denied` and the signed denial receipt is available at the endpoint in
section 4. The elevated token is for the bound group only and expires in 300 s from issue, so the backend retries the wallet call immediately
after the grant succeeds; if it still gets a 401 it starts a new request instead of reusing the consent.

## 2. `authorization_details` types

- **Purpose:** what the phone shows and what the receipt signs. **Fallback:** none. Built by OIDC from the decoded group or request
  body; client-supplied amounts or payees are never accepted.

```json
{
  "type": "biatec_transaction",
  "network": "algorand-testnet",
  "group_hash": "sha256:9c1f...",
  "sender": "ADDR...",
  "transactions": [
    { "kind": "asset_transfer", "to": "POOLADDR...", "asset": 123, "amount": "5000000000", "fiat": { "currency": "EUR", "value": "5000.00" } },
    { "kind": "application_call", "app_id": 456, "method": "addLiquidity", "pool": { "fee_tier": "0.30%", "range": "0.98-1.02" } }
  ],
  "value_eur": "10000.00",
  "origin": "https://dex.biatec.io"
}
```

```json
{
  "type": "biatec_limits_change",
  "network": "algorand-testnet", "address": "ADDR...",
  "current": { "daily_eur": "500" }, "requested": { "daily_eur": "200" },
  "request_hash": "sha256:ab12... (of the RFC 8785 canonical request body)"
}
```

`group_hash` is the value the wallet endpoint compares with the consented hash. It is `sha256:` plus the hex SHA-256 of the canonical form OIDC derives from the group (proposal for the owners to fix
and publish with a test vector; the Algorand group ID is a different value, carried by `grp`, and the backend checks `grp` itself
and treats `group_hash` as opaque, comparing it only for equality with what OIDC returned). For Bitcoin it is the intent hash
derived from a PSBT carrying prevout data (section 8) and `kind` is `bitcoin_output`. Until the algorithm and test vector are published,
tests compare `group_hash` for equality only.

## 3. Step-up challenge on sign and `PUT /wallet/limits`, and `requireConsent`

- **Purpose:** OIDC is the only decision point. **Fallback:** none. Consent is needed when value exceeds the user's threshold,
  cooling-off or first use applies, the user set "always ask" for this client, or the request carried `requireConsent: true`.
  The backend may only set `requireConsent` (more consent), never skip it.
- **Request:**

```http
POST /wallet/algorand-testnet/ADDR.../sign
Authorization: Bearer <access token>
Content-Type: application/json

{ "transactions": ["<base64 unsigned txn>", "..."], "grp": "<base64 group id>", "requireConsent": false,
  "maxConsentSeconds": 600, "origin": "https://dex.biatec.io" }
```

- **Step-up challenge** (also returned by `PUT /wallet/limits`, bound to the canonical request body hash):

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="insufficient_user_authentication", error_description="consent required", acr_values="biatec:consent"
Content-Type: application/json

{
  "error": "insufficient_user_authentication",
  "error_description": "consent required",
  "acr_values": "biatec:consent",
  "auth_req_id": "ar_8f2c...",
  "binding_code": "4821",
  "expires_in": 300,
  "interval": 2
}
```

`expires_in` is the consent window, never longer than `maxConsentSeconds` (section 16) or the group's remaining validity. The
600-second Confirm gate in the fallback of section 16 bounds how long a built preview may be confirmed, a separate clock.

The backend then polls the CIBA token grant (section 1, authenticated as the confidential client, honouring `interval` and `slow_down`) and retries the same request with the elevated bearer. The wallet endpoint
accepts only a consent bound to the hash of what it is asked to do, and consumes it only if signing proceeds. The mock returns this
exact body so the backend is tested against the real shape.

## 4. Device enrollment, attestation, recovery, and receipts

- **Purpose:** authenticator support and evidence. **Fallback:** none.
- Enrollment (authenticator to OIDC, user authenticated): device public key (Secure Enclave or StrongBox), platform attestation,
  push token. Devices become active after an activation delay; a recovery code is issued once, after activation (not in the enrollment response). `active_at` is epoch seconds and `device_token`
  authenticates later `Authorization: Device <token>` calls.

```http
POST /devices
{ "kid": "biatec-authenticator-ab12", "public_key": "<jwk>", "attestation": { "format": "apple-appattest", "data": "<b64>" }, "push": { "provider": "fcm", "token": "..." }, "name": "Pixel 9" }

201 { "kid": "biatec-authenticator-ab12", "active": false, "active_at": 1792486800, "attestation": "<jws>", "device_token": "<opaque>" }
```

- Recovery code (protocol rule 7): the enrollment response above must not carry `recovery_code`; whoever enrolls a device
  could otherwise use the code to skip the delay. The code is issued once the device is active, through
  `POST /devices/recovery-code` (`Authorization: Device <token>`): `403` with `reason: device_inactive` while inactive, `200 { "recovery_code" }`
  (the same code until acknowledged), then `POST /devices/recovery-code/ack` after which the endpoint answers `409`. The kid in
  `POST /devices` is chosen by the device and must be unique (random per install). The mock implements this fallback.
- Authenticator side (all `Authorization: Device <token>`, never from the push body), as the authenticator (T-052) and
  the mock implement it:
  - `GET /devices/requests` → `200 { "requests": [<consent request>] }`: pending, unexpired requests of the device's user.
  - `GET /consent/requests/{auth_req_id}` → the consent request object (`schemas/consent-request.schema.json`), never the
    binding code.
  - `POST /consent/requests/{auth_req_id}/receipt` with the signed receipt envelope (approve, deny and code_failed are all
    signed). Answers: `200 { "status": "approved" | "denied" | "code_failed" }`; wrong code
    `400 { "error": "wrong_code", "attempts_remaining": n }`; the third wrong code, or any decision after it other than the
    `code_failed` receipt, `403 { "error": "access_denied", "reason": "code_failed" }`; already decided `409`; expired `410`;
    inactive device `403`.
  - The attestation JWS returned by `POST /devices` is stored by the device and sent back in every receipt.
  - `PUT /devices/push` with `{ "provider": "fcm" | "apns", "token": "..." }` → `200`: registers or replaces where
    the device's notifications go. For `fcm` the token is the **Firebase Installation ID** that `firebase_messaging`
    17 reports from `register()` / `onRegistered` (its direct-send target), not a legacy registration token, so
    delivery must address it accordingly (the same `push` object `POST /devices` accepts). Called on app start, on token
    rotation, on app resume and on navigation, re-sent at most every 10 minutes (T-053). A token belongs to one
    device: registering it on a device removes it from any other device record.
- Push delivery: when a consent request opens, Biatec OIDC notifies every active device of the user that has a push
  token. The data payload is `{ "auth_req_id": "<id>" }` and nothing else; the visible notification text is a fixed,
  localizable string with no amount, asset, address or client name. The authenticator reads only `auth_req_id` and
  fetches the request with its device token. The mock records these messages instead of sending them
  (`GET /mock/push-outbox`).
- Receipt retrieval, by the client that opened the request (direct entry) or whose token was challenged (step-up):

```http
GET /consent/receipts/ar_8f2c...
Authorization: Bearer <client token>

200 <same object as consent_receipt in section 1>
```

A step-up receipt has `binding_message: ""`; a direct-entry receipt carries the text only when the client chains to a trust anchor.
Receipts verify offline against the device public key and attestation. The backend stores the receipt verbatim.

## 5. Algo Safe application-call valuation

- **Purpose:** threshold on safe approvals. **Fallback:** unknown application calls are above threshold (always ask).
- OIDC values by method: `approveProposal` at the referenced payload read from the safe's boxes; `executeProposal` at zero; funding
  payments as ordinary transfers. The valued payload appears in `authorization_details`:

```json
{ "kind": "application_call", "app_id": 789, "method": "approveProposal",
  "safe": { "proposal_id": 12, "payload": [ { "kind": "payment", "to": "ADDR...", "amount": "100000000", "fiat": { "currency": "EUR", "value": "20.00" } } ] },
  "value_eur": "20.00" }
```

Here `value_eur` on the entry is the per-call value and the top-level `value_eur` of section 2 is the sum over the group.

- **Proposer's approval (found in T-033):** the deployed algo-safe contract records the proposer's own approval inside
  `proposeTransactionGroup`, so the proposer never sends a separate `approveProposal` (it would be refused as "already approved").
  The spec's "propose, then approve as a second call" does not exist on chain. **Needed:** value `proposeTransactionGroup` at its
  payload (decoded from the call's own `payload` argument, which is the exact `(uint64,byte[])[]` the safe stores) instead of at
  zero, so the proposer's consent lands where the authorization happens. `proposeAdminChange` stays at zero.
  **Fallback in use:** the chat card says that proposing records the proposer's approval; under "always ask" (the demo fallback)
  the phone still asks for the propose call, but it shows no payload value; under testnet valuation it would not ask at all.

## 6. Biatec DEX pool call decoding

- **Purpose:** pool facts on the phone card. **Fallback:** treat as an unknown application call (always ask), showing decoded
  transfers and the application ID only.
- OIDC recognizes the pool provider's pools, reads fee tier and price range from global state, and values the group's transfers.
  Output is the `pool` object in the `application_call` entry of section 2; unrecognized pools omit it.

## 7. Test-network valuation from a static price table

- **Purpose:** thresholds fire in the testnet demo and CI, for Algorand and Bitcoin test networks. **Fallback:** demo identities use
  "always ask" for the Biatec Chat client and no spending limits.
- OIDC-side configuration (proposed shape):

```json
{ "TestnetPrices": { "algorand-testnet": { "ALGO": "0.20", "asset:10458941": "1.00" }, "bitcoin-testnet": { "BTC": "60000" } }, "Currency": "EUR" }
```

## 8. Group-hash binding in the signing endpoint

- **Purpose:** "one byte different, refused". **Fallback:** none; the protocol is unsafe without it.
- Algorand: recompute the group ID from the handed transactions and refuse a mismatching `grp`. Bitcoin: derive the intent hash
  from a PSBT with prevout data. Compare either with the consented hash before signing.

```http
400 Bad Request
{ "error": "group_hash_mismatch", "error_description": "grp does not match the handed transactions" }

403 Forbidden
{ "error": "consent_hash_mismatch", "error_description": "elevated token is bound to a different group" }
```

## 9. Confidential-client dynamic registration for tenants

- **Purpose:** self-service tenant onboarding for tenants that pass domain verification. **Fallback in use:** an operator adds each
  tenant's client (`Tenant.OidcClientId`, authorization-code grant, S256 PKCE, `resource` `https://mcp.biatec.io/tools`, redirect
  URI `Tenant.RedirectUri`) to Biatec OIDC's static client list. The client secret is supplied to the backend as configuration
  `Secrets:<path of Tenant.ClientSecretRef>` (for example `Secrets__biatec-chat__oidc-client-secret`), never in the repo.
- **Status:** as at the top of this file: automated tests run against the in-repo mock OIDC only; stage uses the `OIDC_CLIENT_ID` client at
  stage.oidc.biatec.io ([cicd.md](cicd.md#secrets)); a production registration on oidc.biatec.io is not confirmed in this repository.
- **Registration request for the Biatec Chat tenant, once the consent protocol ships** (RFC 7591; sent by an operator, or accepted by a future confidential
  registration endpoint; secret values are never committed):

```http
POST /register
Content-Type: application/json

{
  "client_name": "Biatec Chat",
  "client_uri": "https://chat.biatec.io",
  "redirect_uris": ["https://chat.biatec.io/auth/callback"],
  "grant_types": ["authorization_code", "refresh_token", "urn:openid:params:grant-type:ciba"],
  "response_types": ["code"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access sign",
  "subject_type": "pairwise",
  "backchannel_token_delivery_mode": "poll",
  "authorization_details_types": ["biatec_transaction", "biatec_limits_change"],
  "post_logout_redirect_uris": ["https://chat.biatec.io/"],
  "software_id": "biatec-chat"
}
```

```json
{ "client_id": "biatec-chat", "client_secret": "<issued once, goes to the secret store>", "client_secret_expires_at": 0,
  "registration_client_uri": "https://oidc.biatec.io/register/biatec-chat", "registration_access_token": "<store like the secret>" }
```

The redirect URI must equal the tenant's `Tenant.RedirectUri` as implemented by `/auth/callback`. **Registration today** (what an operator enters on the static client list while sections 1, 2 and 15 are on their fallbacks):
the same request without `urn:openid:params:grant-type:ciba` in `grant_types`, without `subject_type`,
`backchannel_token_delivery_mode` and `authorization_details_types`, and with `scope` `openid profile email offline_access sign` (the client requests `sign` at login and, if it was not granted, at elevation; the registration must allow it). The
backend and mock assume a public `sub`; adding the omitted fields later is a registration update, not a code change.

Scopes `manage-limits` and
`rekey` are added only for operator-allowlisted clients. Domain verification (DNS or federation challenge) is checked by the
operator before registration. Until the endpoint accepts confidential clients, `POST /register` keeps issuing public clients only and
this request is processed by hand.

## 10. Reduced per-client daily cap without an active device

- **Purpose:** rule 4. **Fallback:** the backend enforces the reduced 100 EUR per-client daily cap for users with no active device
  and blocks above-threshold signing for them (weaker, because backend-side).
- OIDC side: if the user has no active device (`device_active: false` in section 20), the signing endpoint applies the reduced cap
  and refuses with HTTP 403 `{ "error": "daily_cap_exceeded", "cap_eur": "100", "reason": "no_active_device" }`.
- Backend fallback (T-061, `backend/Biatec.Chat.Tools/Caps/NoDevice/`): before every signing request the confirm path reads
  `GET /consent/settings` (row 20). When it answers `device_active: false` (an enrolled device still in its activation delay
  counts as none):
  - an action that would need the phone (value above `threshold_eur`, clamped to the 1000 EUR ceiling and the ceiling when OIDC
    omits it; a value the backend cannot predict; `always_ask`; a preview's own `requireConsent`; or a soft or safe cap
    forcing consent) is refused with `no_active_device` and an enrollment hint, before anything is reserved or signed.
    Cooling-off (`cooling_off_until`) and `first_use` do not block such a user: per rules 4 and 8 the reduced daily cap applies
    instead;
  - if Biatec OIDC (which does not implement rule 4 yet) answers such a user's signing request with a step-up challenge, no
    consent request is opened: the reservation is released, the preview goes back to awaiting confirmation, `signing_refused`
    is recorded and the user gets `no_active_device` with the enrollment hint at once;
  - otherwise the action's value is reserved in the T-025 store (`SoftCapSpends`, same advisory lock as the soft cap) against a
    rolling 24-hour own-funds total per user `sub` and tenant; over `Caps:NoDevice:DailyCapEur` (default 100) it is refused with
    `no_device_daily_cap` and the enrollment hint. Safe approvals and executions add nothing, as in the soft cap, and a
    zero-value action is never refused by it. While the cap is enabled, final rows are kept 25 hours (otherwise 2 hours).
    Without a signed-in `sub` or a cap store the cap cannot be checked: refused with `no_device_cap_unavailable`.
  - The backend applies the reduced cap every day, not only during the client's first 24 hours and the user's first day with the
    client as rule 4 says for OIDC: it has no record of those dates, so it takes the stricter reading.
  - If `GET /consent/settings` is missing or fails, nothing changes (OIDC's step-up is relied on). An answer is cached per
    session for 60 seconds per pod; a 404 or 501 is remembered for 10 minutes and the endpoint is not called meanwhile.
  - Config: `Caps:NoDevice:Enabled` (default true) and `Caps:NoDevice:DailyCapEur` (default 100). Turn `Enabled` off once Biatec
    OIDC ships the 403 `daily_cap_exceeded` / `no_active_device` rule above; the backend already passes such a refusal through as a
    rejected signing request.

## 11. `valuedEur` in every signing response

- **Purpose:** the soft cap counts what OIDC valued. **Fallback:** the backend counts its predicted value (recording
  `valueSource`), documented as only as good as the backend's price feed.

```json
{ "signedTransactions": ["<base64>", "..."], "valuedEur": "10000.00" }
```

`"0.00"` for zero-valued calls and `null` if the network cannot be priced (the backend then falls back and records it).

## 12. OpenID Federation trust-chain evaluation for client display names

- **Purpose:** impostor protection. **Fallback:** Biatec's own clients are pinned as verified by configuration; every other client
  is shown unverified.
- OIDC evaluates the client's entity configuration against a recognized trust anchor and uses the result for the authenticator card.
  Only verified clients get a rendered `binding_message`; unverified ones sign the empty string.

```json
{ "client_id": "biatec-chat", "entity_id": "https://chat.biatec.io", "display_name": "Biatec Chat", "verified": true, "trust_anchor": "https://biatec.io" }
```

## 13. Forced fresh consent on scope elevation

- **Purpose:** keeps the `sign` consent screen visible if consent memoization is ever introduced. **Fallback:** not needed while
  every authorization shows the consent screen.
- Requirement: an authorization request with `prompt=consent` (used by the elevation flow) shows the screen even if a prior grant
  exists. A memoized grant never satisfies a request for a wider scope.

```http
GET /connect/authorize?...&scope=openid%20sign&prompt=consent
```

## 14. RFC 7009 revocation of exactly the presented refresh token

- **Purpose:** logout and scope elevation hygiene. **Fallback:** destroy the vault entry and let the token age out.

```http
POST /connect/revocation
Authorization: Basic <client credentials>

token=<refresh token>&token_type_hint=refresh_token

200 (empty body, also for unknown tokens)
```

Requirement: only the presented token is revoked, not the whole grant for the client and subject, so the token issued by a scope
elevation survives revocation of the one it replaced.

## 15. Pairwise subject identifiers per tenant client

- **Purpose:** receipts and evidence carry no globally linkable identifier. **Fallback:** public `sub`; deletion cannot unlink
  retained receipts (documented).
- Client metadata `subject_type: pairwise` (see section 9). Discovery lists `subject_types_supported: ["public","pairwise"]`. The
  `sub` in ID tokens, tokens and receipts is then stable per tenant client, not across clients.

## 16. `maxConsentSeconds` on the signing request

- **Purpose:** consent cannot outlive the group. **Fallback:** rely on the 600-second Confirm gate and rebuild more often.
- Request field (see the request in section 3). OIDC sets the CIBA `expires_in` to the minimum of `maxConsentSeconds`, its own
  default and the group's remaining validity. A value below a minimum (for example 30) is refused with HTTP 400
  `{ "error": "invalid_request", "error_description": "maxConsentSeconds too small" }`.

## 17. Origin-aware consent requests

- **Purpose:** one compromised Biatec site cannot block or pause the others. **Fallback:** slots and pauses apply per client for
  all Biatec sites together.
- The backend sends the page origin, validated against the tenant's allowed origins and never raw client input, in the `origin` field
  of the signing request (`biatec_origin` on CIBA direct entry). OIDC scopes the three-pending-request limit, rate limits and
  24-hour pauses by client and origin, and shows the origin on the card and in `authorization_details.origin`.

```http
429 Too Many Requests
{ "error": "too_many_pending_requests", "limit": 3, "scope": { "client": "biatec-chat", "origin": "https://dex.biatec.io" } }
```

## 18. Per-client refresh token lifetime with absolute-expiry rotation

- **Purpose:** shrink the window a stolen vault is useful for. **Fallback:** keep 30-day tokens; revocation (row 14) is not available, so the vault entry is destroyed at logout (residual
  exposure 30 days).
- Client metadata (proposed name) `refresh_token_lifetime_seconds: 28800`. On rotation the new refresh token inherits the original
  absolute expiry:

```json
{ "access_token": "...", "refresh_token": "...", "expires_in": 900, "refresh_token_expires_at": "2026-10-20T17:00:00Z" }
```

A rotated token's expiry is never later than the first token's `refresh_token_expires_at`. Reuse detection applies as today; the
backend single-flights refreshes so it never fires on legitimate traffic.

## 19. Consent-gated loosening of consent settings

- **Purpose:** an identity-provider-only attacker cannot disable the phone. **Fallback:** settings can be loosened only from an
  active enrolled device's own screen, not from the web; chat hides raising limits (`limits.update` is lowering-only).
- Requirement: raising the threshold, removing "always ask", relaxing per-client settings and unpausing a client require a consent
  receipt from an active device (the same CIBA request model with a proposed `biatec_settings_change` type), never a web session
  alone. Tightening needs no consent. A loosening attempt without it answers the same challenge, but it is not a retryable wallet call: the backend does not poll for it
(the settings change is only completed on the device's own screen), and the chat reports "change this on your phone":

```http
401 Unauthorized
{ "error": "insufficient_user_authentication", "acr_values": "biatec:consent", "auth_req_id": "ar_...", "binding_code": "7310" }
```

## 20. `GET /consent/settings`

- **Purpose:** the backend predicts on the preview card whether the phone will be asked, and tells users without a device to
  enroll. **Fallback:** the card says "may need approval on your phone" without predicting.

```http
GET /consent/settings
Authorization: Bearer <access token>
```

```json
{
  "threshold_eur": "250.00",
  "device_enrolled": true,
  "device_active": true,
  "client": { "always_ask": false, "paused_until": null, "cooling_off_until": null, "first_use": false, "per_client_threshold_eur": null }
}
```

The response is for the calling client. The backend's prediction adds its own triggers (soft cap, safe caps via `requireConsent`)
and never reports less consent than OIDC would require. If the call fails, the fallback card text is used.

## 21. Device list and cancelling a pending enrollment (T-084)

- **Purpose:** the authenticator's device management screen (spec, Authenticator App) and rule 7: a pending enrollment "can be
  cancelled from that device". **Fallback:** none. The app has the config flag `--dart-define=CONSENT_SETTINGS_API=true|false`;
  it defaults to on for the mock URL and off for every other Biatec OIDC, where the three settings screens show "Not available yet"
  and make no call.

```http
GET /devices
Authorization: Device <device token>
```

```json
{ "devices": [ { "kid": "alice-device", "current": true, "active": true, "active_at": 0 },
               { "kid": "phone-2", "current": false, "active": false, "active_at": 1792512000 } ] }
```

`current` marks the caller; only the caller's user's devices are listed; cancelled devices are not.

```http
POST /devices/{kid}/cancel
Authorization: Device <device token of any device of the same user, active or pending>
```

`200 {"kid": "...", "cancelled": true}`; `409` for an active device (only pending enrollments cancel); `404` for another user's or an
unknown device. A cancelled device's token stops working (`401`) and it gets no push. Cancelling only shuts a device out, so it
needs no consent.

## 22. Device-side consent settings with device-signed loosening (T-084)

- **Purpose:** the security settings and trusted-clients screens, and row 19: loosening needs a receipt from an active device.
  **Fallback:** none; same `CONSENT_SETTINGS_API` flag as above.
- The CIBA request model with a `biatec_settings_change` type (row 19) is the web-initiated path. On the device's own screen the
  change is signed directly, which this section proposes: no round trip through `auth_req_id`.

```http
GET /devices/settings
Authorization: Device <device token>
```

```json
{
  "sub": "pw_alice",
  "biometric_required": true,
  "threshold_eur": "1000.00",
  "block_unverified_clients": false,
  "clients": [ { "client_id": "biatec-chat", "label": "Biatec Chat", "verified": true, "always_ask": false,
                 "per_client_threshold_eur": null, "paused_until": null } ]
}
```

`paused_until` is epoch seconds while the 24 hour client pause of rule 10 runs, else `null`; `per_client_threshold_eur` `null` follows
`threshold_eur`.

```http
PUT /devices/settings
Authorization: Device <device token>
Content-Type: application/json

{ "change": { "threshold_eur": "800", "clients": { "biatec-chat": { "always_ask": false, "paused": false } } },
  "consent": { "signed": { "type": "biatec_settings_change", "sub": "pw_alice", "kid": "alice-device",
                           "nonce": "9f1c...", "issued_at": 1792425600, "change": { "...": "same as change" } },
               "alg": "ES256", "signature": "<base64url r||s over JCS(signed)>" } }
```

- `change` members, all optional, at least one: `threshold_eur` (decimal string), `biometric_required`, `block_unverified_clients`
  (booleans), `clients.{id}.always_ask` (boolean), `clients.{id}.per_client_threshold_eur` (decimal string, or `null` to follow
  the account threshold), `clients.{id}.paused` (only `false`: lift the pause). Unknown members answer `400`.
- **Tightening** (lower a threshold, turn a requirement on, set always-ask) applies at once with no `consent`.
- **Loosening** (raise a threshold, turn `biometric_required`, `block_unverified_clients` or `always_ask` off, lift a pause, or set a
  per-client threshold above the effective one) needs `consent`: signed by the device key of an **active** device (rule 7) over
  JCS of `signed`; `signed.change` must equal `change`, `sub` and `kid` must be the caller's, `issued_at` within 300 seconds, and the
  `nonce` unused for that device. A mixed change needs consent as a whole. Otherwise
  `403 {"error": "insufficient_user_authentication", "acr_values": "biatec:consent", "reason": "consent_required"}` and nothing changes.
- `200 {"applied": true, "loosened": bool, "settings": <GET body>}`.
- The device classifies a change itself to decide whether to ask for the biometric check and sign; if Biatec OIDC answers
  `consent_required` for a change the device judged tightening, the device signs and retries once.

## 23. Operator identity claim for the admin API (T-097)

- **Purpose:** every admin API change (tenant policy, knowledge-base pages) is attributed to a named person, with the
  identity proven by Biatec OIDC instead of a bearer key the backend has to manage, and revocable centrally (an
  operator who leaves loses access when the IdP removes the group).
- **Needed:** an operator group or claim that Biatec OIDC asserts only for Biatec staff, for example in the ID token and
  token introspection of a dedicated confidential client `biatec-chat-admin`:

```json
{
  "sub": "op_7f3a…",
  "preferred_username": "alice",
  "groups": ["biatec_operator"],
  "acr": "biatec:mfa",
  "auth_time": 1792425600
}
```

  The admin API would accept an access token for `resource` `https://chat.biatec.io/admin` (audience-bound, short-lived,
  at most 15 minutes) whose subject is in `biatec_operator` and whose `acr` shows MFA, and record `sub` and
  `preferred_username` as the operator. Names (`biatec_operator`, `biatec:mfa`, the client and resource) are proposals
  for the OIDC owners to confirm. Tokens stay with the operator's CLI; the browser widget is not involved.
- **Fallback in use:** named operator keys. Each operator has an own random bearer key (at least 32 characters); the
  backend is configured with only its SHA-256 as `Admin:Operators:<name>:KeyHash` (from the secret store), compares in
  constant time, and records `<name>` with `operatorAuth: "named-key"` in the signed admin evidence chain
  (`docs/admin-api.md`, Admin evidence chain). The former single shared key `Onboarding:OperatorKey` is still accepted
  as the deprecated operator `legacy`. Revoking an operator means removing their entry and redeploying.
- **Mock:** none needed; the fallback does not call Biatec OIDC. When the claim ships, the mock OIDC grows the
  `groups` claim and the admin API gains a token check next to the key check.

## Administrator dashboard sign-in (T-126)

- **Needed:** the ID token of a Biatec OIDC login carries the `email` claim and `email_verified: true` (boolean or the string `"true"`) for the scope `openid profile email`.
  The dashboard ([admin-dashboard.md](admin-dashboard.md)) admits a user only when that verified email is on the `ADMINS` list, so its security is exactly the strength of Biatec OIDC's
  proof of an email address.
- **Fallback in use:** `Admin:RequireVerifiedEmail` (default `true`). If Biatec OIDC does not issue `email_verified`, admin sign-in fails with `email_not_verified`; an operator can set
  `Admin__RequireVerifiedEmail=false`, accepting that the email claim is then trusted as is. No change to Biatec OIDC is needed otherwise: the dashboard reuses the tenant's registered
  client and redirect URI.
- **Status:** tested against the in-repo mock OIDC only (`AdminDashboardTests`); not confirmed against oidc.biatec.io.

## Popup pages and `window.opener` (BCHAT-028)

- **Exposure:** the widget opens sign-in, elevation and sign-out in a popup with `window.open`, so every page that popup shows can reach the host page through `window.opener` (reverse tabnabbing: for example `window.opener.location = ...`). The backend's own pages in the flow are a redirect and a fixed close page; the pages that matter are Biatec OIDC's authorization and consent pages.
- **Needed:** Biatec OIDC sends `Cross-Origin-Opener-Policy: same-origin` on its authorization, login, consent and logout pages. That severs the opener relation for the rest of the popup's life.
- **Prerequisite in this repository:** with the header on, the opener sees `popup.closed === true` immediately, and `useChat` reads that as "the user closed the popup" and fails the sign-in after one more status poll. Before the header is switched on, the widget must stop using `popup.closed` as the cancel signal (the status polling and the 5 minute timeout already end a flow; a Cancel button while waiting would give the user the quick way out), and a severed popup closes itself through the close page's `window.close()` or is left for the user. Release the widget first, then ask for the header.
- **Fallback in use:** none; the exposure needs a compromised Biatec OIDC (or backend) page, which is why the finding is rated Low. No change to the popup flow was made.

