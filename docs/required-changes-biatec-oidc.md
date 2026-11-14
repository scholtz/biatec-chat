# Required changes to Biatec OIDC: handoff spec and fallback matrix

This is the handoff for the Biatec OIDC owners and the matrix biatec-chat agents implement against. It covers every row of the
"Required Changes To Biatec OIDC" table in `PRODUCT-DESCRIPTION.md` (the specification, which wins on any conflict). We work only
in this repository, so for each row this file states the API shape the product expects and the fallback this repo implements now.
Examples are illustrative: names the spec fixes are normative (`insufficient_user_authentication`, `biatec:consent`,
`auth_req_id`, `consent_receipt`, `requireConsent`, `valuedEur`, `maxConsentSeconds`, `authorization_details`, the
`biatec_transaction` and `biatec_limits_change` types); other names are proposals for the OIDC owners to confirm.

## Protocol 1.1 (T-239): what Biatec OIDC must provide

The consent protocol is now version 1.1 (`packages/consent-protocol/spec/biatec-consent-protocol.md`, "Changes from 1.0"). Where an older row below
shows a 1.0 shape (the `sha256:` form of `group_hash`, a `verified` client with a display name, a single transfer with one `fiat_value`, one device key),
the 1.1 shape in the spec and the schemas wins. The implementation plans are the issues of ludovit-scholtz/AlgorandGoogleDriveAccount#23 (CONSENT-01 to
CONSENT-15); biatec-chat's T-240 brought this document row by row up to date. Where a row names an issue, "#n" is an issue of that repository
(`ludovit-scholtz/AlgorandGoogleDriveAccount`, the Biatec OIDC repository; the `scholtz/` copy is stale).

| Biatec OIDC must provide | Issue | Fallback in this repository |
| --- | --- | --- |
| `group_hash` as the recomputed Algorand group id; `transfers`, `applications`, `value_known`, signed `origin` in `authorization_details` | #26, #28 | None: build against the mock only (T-240); against real Biatec OIDC the feature is disabled by configuration: keep consent enforcement off against real Biatec OIDC (the switch is Biatec OIDC's own `Consent__Enforcement`, default `Off`; the backend has no switch of its own: it asks the phone only when Biatec OIDC answers 401 `insufficient_user_authentication`, and its caps and no-device rules apply until the two settings of [consent-rollout.md](consent-rollout.md) are changed) |
| Client identity as `{ fingerprint, domain_proven, domain?, linked }`; the domain proof `/.well-known/biatec-client.json`; no operator-vouched name | #25, #27 | None: mock only (T-240), disabled by configuration against real Biatec OIDC (the switch is added by that task); the backend serves the well-known file so the proof works when Biatec OIDC checks it |
| Two attested device keys (`biatec_key_use` `approve` / `presence`); an approve receipt refused unless its attestation says `approve` | #32 | None: mock only (T-241), disabled by configuration against real Biatec OIDC (a build flag added by that task keeps the app on one key); the real server keeps accepting 1.0 single-key receipts until it ships |
| Enrollment proof (`biatec-device-enroll+jwt`), pairing code, `POST /devices/{kid}/activate` with a signed `biatec_device_approval` | #31 | None: mock only (T-242), disabled by configuration against real Biatec OIDC (the in-app login and the activation by another device are off; enrollment keeps the 24 hour delay) |
| Settings changes signed by the approval key (`biatec_settings_change`), threshold with no ceiling, trusted applications, `remember` on an approve receipt | #27, #30 | None: mock only (T-245), disabled by configuration against real Biatec OIDC (the settings screens stay read-only); the chat backend keeps its own `GET /consent/settings` fallback |
| Device channel (SignalR) instead of a third-party push; email through Azure Communication Services | #29, #37 | None: the device channel and the email are switched off by configuration until Biatec OIDC provides them; the app polls every 5 seconds while open and no email is sent |
| Tenant fields `consentThresholdCeilingEur` and `TrustAnchorVerified` keep their names until T-240 renames them (they now mean the deployment's default consent threshold and "the domain proof was seen"; the admin API and the deploy workflow expose them) | none | Keep the names; the specification's wording wins |
| Relying parties (self-registration, linking, payment and login requests) | #38 | None: mock only (T-246, T-247); disabled by configuration against real Biatec OIDC |

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

| # | Addition | Biatec OIDC issue | Fallback implemented in this repo now |
| --- | --- | --- | --- |
| 1 | CIBA backchannel authentication endpoint and token grant | #28 (the token grant of a step-up), #34 (direct entry, discovery, grant restriction per client) | None. Consent flows run only against the mock; above-threshold signing is unavailable against real OIDC until it ships |
| 2 | `authorization_details` types `biatec_transaction`, `biatec_limits_change` | #26 (the transaction group), #33 (limits) | None. Mock builds them from the group |
| 3 | Step-up challenge on sign and `PUT /wallet/limits`, `requireConsent` | #28 (sign), #33 (limits) | None. Mock returns the challenge; on the wallet signing and limits endpoints the backend treats a 401 `insufficient_user_authentication` with an `auth_req_id` as the only consent trigger, and retries the same request after the poll. `requireConsent` (set by the soft cap and safe caps, T-025) is sent only when `Caps:OidcHonoursRequireConsent` is true (mock only; default false): against real Biatec OIDC an action a cap would send to the phone is refused instead, so the cap is a hard stop for the rest of the window. Whenever `requireConsent` was sent and OIDC signs without a challenge, the backend refuses to submit (`consent_not_enforced`) |
| 4 | Device enrollment, attestation, receipts, `GET /consent/receipts/{auth_req_id}` | #24 (one account key), #31 (enrollment), #32 (two keys), #36 (platform attestation) | None. Authenticator and backend develop against the mock. Rules the mock models and the real server must keep (BCHAT-024): a receipt is returned only to a bearer whose subject owns the request (a client credential may fetch the receipts of its own requests); `slow_down` to a client that polls faster than `interval` and +5 s on the interval after each; a bounded number of open requests per user (429); a device that is not active yet (rule 7) can neither change settings, tightening included, nor cancel another enrollment (it may cancel itself) |
| 5 | Algo Safe application-call valuation: no operator valuation any more; an application the user did not trust makes the value unknown | #26 (applications and `trusted`), #27 (the user's trusted applications) | An application call is listed on the card and, until the user trusts it (`remember` on an approval), the value is unknown, so the phone is asked |
| 6 | Biatec DEX pool call decoding: not part of protocol 1.1 | none | The card shows the decoded transfers and the application id only |
| 7 | Test networks: no operator price table; the user's `ask_on_test_networks` setting decides whether an unpriced test-network transfer asks the phone | #26 (unpriced), #27 (the setting) | The mock prices ALGO and USDC with a static table of its own; demo identities may set "always ask" |
| 8 | Group-hash binding in the signing endpoint | #26 (recomputed group id), #28 (binding, reserve and spend) | None. The backend also recomputes the group ID before calling, as defence in depth, not a substitute |
| 9 | Confidential-client registration for tenants | none | Operator adds the tenant client to the static client list (section 9) |
| 10 | Reduced per-client daily cap without an active device | #27 (policy), #28 (the refusal) | Backend enforces the 100 EUR per-client daily cap and blocks above-threshold signing for users with no active device (T-061, `Caps:NoDevice`). "No active device" is known only through the backend's own enrollment record or row 20; where neither exists, the backend cannot tell and relies on OIDC's step-up challenge. Set `Caps:NoDevice:Enabled` to false once Biatec OIDC enforces the rule itself |
| 11 | `valuedEur` in every signing response | #28 | Backend counts its own predicted value in the soft cap; only as good as its price feed |
| 12 | Client identity for consent: a fingerprint and a proven domain (`/.well-known/biatec-client.json`); no verified flag, no operator-set name, no federation | #25 | The backend serves the well-known file for the tenant bound to the request host; until Biatec OIDC checks it the card says "Unproven application" |
| 13 | Forced fresh consent on scope elevation | none | Not needed: every authorization shows the consent screen |
| 14 | RFC 7009 revocation of exactly the presented refresh token | none | Destroy the vault entry and let the token age out; deployment security notes say so. Residual risk: a refresh token stolen earlier stays valid at the IdP until expiry (see row 18) |
| 15 | Pairwise subject identifiers per tenant client | #24 (the account key that joins pairwise subjects) | Public `sub` used; deletion cannot unlink retained receipts (documented) |
| 16 | `maxConsentSeconds` on the signing request | #28 | Rely on the 600-second Confirm gate and rebuild more often |
| 17 | Origin-aware consent requests | #28 (origin on the request), #30 (the 24 hour pause per origin) | Pending slots, rate limits and pauses apply per client for all Biatec sites together; the backend treats HTTP 429 as "try later" in the widget |
| 18 | Per-client refresh token lifetime (8 h) with absolute-expiry-preserving rotation | none | Keep 30-day tokens, destroy the vault entry at logout (no IdP revocation, row 14); residual exposure at the IdP is 30 days |
| 19 | Consent-gated loosening of consent settings | #30 | Loosening only from an enrolled device's own screen; chat hides raising limits (`limits.update` is lowering-only) |
| 20 | `GET /consent/settings` | #27 | Card says "may need approval on your phone" without predicting |
| 21 | Device list and cancelling a pending enrollment from a device (`GET /devices`, `POST /devices/{kid}/cancel`) | #30 | None. The authenticator's device screen runs only against the mock; `CONSENT_SETTINGS_API` is off for any other Biatec OIDC and the screen then says "not available yet" (T-084) |
| 22 | Device-side consent settings (`GET` / `PUT /devices/settings`) with device-signed loosening | #30 | None. The security and trusted-clients screens run only against the mock, behind the same `CONSENT_SETTINGS_API` flag (T-084) |
| 23 | Operator identity claim (`biatec_operator` group) for the admin API | none | Named operator bearer keys: `Admin:Operators:<name>:KeyHash` holds only the SHA-256 of each operator's key, and every admin change records the operator name in the signed admin evidence chain (T-097, docs/admin-api.md). The shared `Onboarding:OperatorKey` is still accepted as the deprecated operator `legacy` |
| 24 | Minimum authenticator build: every answer carries `X-Biatec-Min-App-Build` | #30 | An optional build expiry date compiled into the app (`--dart-define=BUILD_EXPIRES`); without either an old build keeps running. The authenticator reads the header on every call it makes, against the mock only until Biatec OIDC sends it (T-232) |
| 25 | Platform attestation at enrollment: a challenge endpoint, and Play Integrity and App Attest tokens verified (`POST /devices/challenge`, `attestation_challenge` and `platform_attestation` on `POST /devices`, `PUT /devices/attestation`) | #36 (verification), #31 (the challenge endpoint) | Enroll without an attestation and flag the device; the authenticator sends one when the platform offers it and the mock checks its shape (T-234) |

## Status on Biatec OIDC (T-243, as of 2026-11-14)

Refresh it with `gh issue list -R ludovit-scholtz/AlgorandGoogleDriveAccount --state all --limit 60` (and `git log` of that repository's `master`). Row by row: whether the change is implemented in Biatec OIDC, and whether it is enabled on stage. The source is the issue list of
`ludovit-scholtz/AlgorandGoogleDriveAccount` (`gh issue list --state all`: issues 23 to 38 are all open, nothing of the consent architecture is closed) and the
commits on that repository's `master`, which holds part of the foundation: the consent protocol primitives (PR 19), consent requests, device endpoints and
receipt verification (PR 20), consent polling with `slow_down` and `access_denied` (PR 21), the CIBA token grant with the elevated consent token (PR 22, for
issue 15), the device API with proof of possession (PR 18, for issue 12) and the popup opener policy (PR 17, issue 13). "Enabled on stage" means this repository
switches the feature on there (`k8s/stage`, the authenticator build flags); **no row is enabled on stage**, because `Consent__Enforcement` and the backend settings
of [consent-rollout.md](consent-rollout.md) are all at their off values. What is not known from here is whether the stage Biatec OIDC currently runs the master
commits named above (its last stage deploy commit in that repository is 1.2026.11.12-main) and how it is configured; the first conformance run against stage
(consent-rollout.md, part 1) is what answers it. It has not been run.

| # | Implemented in Biatec OIDC (issue) | Enabled on stage |
| --- | --- | --- |
| 1 CIBA endpoint and token grant | Partly: the token grant, polling and the elevated token are on master (PR 21, 22); the step-up of the signing endpoint (#28), direct entry, discovery and grant restriction (#34) are not (open) | No |
| 2 `authorization_details` types | No (#26, #33 open) | No |
| 3 Step-up challenge and `requireConsent` | No (#28, #33 open) | No |
| 4 Device enrollment, attestation, receipts | Partly: device API with proof of possession and receipt store on master (PR 18, 20); account key (#24), enrollment hardening (#31), two keys (#32), platform attestation (#36) open | No |
| 5 Safe application-call valuation | No (#26, #27 open) | No |
| 6 DEX pool call decoding | Not part of protocol 1.1; no issue | No |
| 7 Test networks without a price table | No (#26, #27 open) | No |
| 8 Group-hash binding | No (#26, #28 open) | No |
| 9 Confidential-client registration | No issue; the operator adds the static client (the fallback in use) | The fallback is in use: yes |
| 10 Reduced cap without an active device | No (#27, #28 open) | No: the backend's own rule (`Caps__NoDevice__Enabled=true`) is what runs |
| 11 `valuedEur` | No (#28 open) | No |
| 12 Client identity and domain proof | No (#25 open); the backend serves the proof file (T-240) | No: the phone would say "Unproven application" |
| 13 Forced fresh consent on scope elevation | No issue; not needed | n/a |
| 14 RFC 7009 revocation of the presented token | No issue | No |
| 15 Pairwise subject identifiers | No (#24 open) | No |
| 16 `maxConsentSeconds` | No (#28 open) | No |
| 17 Origin-aware consent requests | No (#28, #30 open) | No |
| 18 Per-client refresh lifetime | No issue | No |
| 19 Consent-gated loosening of settings | No (#30 open) | No |
| 20 `GET /consent/settings` | No (#27 open) | No: `CONSENT_SETTINGS_API` is off for real Biatec OIDC |
| 21 Device list and cancel | No (#30 open) | No |
| 22 Device-side consent settings | No (#30 open) | No |
| 23 Operator identity claim | No issue | No: named operator keys are the fallback in use |
| 24 Minimum authenticator build header | No (#30 open) | No |
| 25 Platform attestation at enrollment | No (#36, #31 open) | No |
| Protocol 1.1 table: device channel (SignalR) and email | No (#29, #37 open) | No: `DEVICE_CHANNEL` off for real Biatec OIDC (T-244), no email |
| Protocol 1.1 table: relying parties | No (#38 open) | No |

**Mock status.** The in-repo mock Biatec OIDC (`packages/consent-protocol/mock-oidc`, T-047 and T-240) implements the consent side as protocol 1.1:
the step-up entry on `POST /wallet/{network}/{address}/sign` and `PUT /wallet/limits`, the direct entry, the CIBA token grant, receipts with two
attested device keys, device enrollment, the device list, settings, `GET /consent/settings`, the refusals of section 3, reserve-then-spend,
`valuedEur`, the minimum build header and the platform attestation challenge. It decodes real unsigned Algorand transactions with `algosdk` and
recomputes the group id. The conformance kit (`packages/consent-protocol/conformance`) checks every refusal and behaviour below against it.
The mock's simplifications are in `packages/consent-protocol/mock-oidc/README.md` (a static price table, zero signatures, no Bitcoin, no device
channel). The login side (authorization code) used by `/auth/login` is mocked separately, by `backend/Biatec.Chat.Tests/Auth/MockOidc.cs`. Statements
below that say "the mock returns this" are what it does today.

Conventions in the examples: `{network}` is a network name such as `algorand-testnet`; bearer tokens are held only by the backend
(the browser never sees them); signing always goes from the backend straight to `POST /wallet/{network}/{address}/sign`.

## 1. CIBA backchannel authentication endpoint and token grant

- **Purpose:** opens and completes consent requests. **Fallback:** none.
- **Entry points:** step-up (OIDC opens the request while answering a wallet call, row 3) and direct (the client opens it first).
  Both produce the same request object: `authorization_details`, binding code, optional `binding_message`, client identity,
  `requested_expiry`, bound hash.
- **Direct-entry request** (client authenticated as at the token endpoint; the parameter names are those of `schemas/backchannel-authentication-request.schema.json`, form-encoded
  on the wire, `unsigned_group` repeated; AlgorandGoogleDriveAccount#34 proposes a `biatec_` prefix for `network`, `address`, `unsigned_group` and `origin`, to be reconciled with the schema):

```http
POST /connect/ciba HTTP/1.1
Content-Type: application/x-www-form-urlencoded
Authorization: Basic <client credentials>

scope=sign&resource=https%3A%2F%2Fmcp.biatec.io%2Ftools
&login_hint=<the client's own sub for this user (pairwise once row 15 ships)>&requested_expiry=300
&binding_message=Add%20liquidity%20to%20EURS%2FUSDC
&network=algorand-testnet&address=ADDR...
&unsigned_group=<base64 unsigned transaction>&unsigned_group=<base64 unsigned transaction>
&origin=https%3A%2F%2Fdex.biatec.io
```

```json
{ "auth_req_id": "ciba_8257c10231dd9b46e2f877484cf0b2a3", "expires_in": 300, "interval": 2, "binding_code": "120934" }
```

The binding code is six digits everywhere (`schemas/binding-code.schema.json`). `binding_message` is rendered, and so signed, only when the client's domain is proven
(row 12); otherwise the signed value is the empty string. A request that carries `authorization_details` is `400`.

- **Token grant** (poll mode only; ping and push delivery are out of scope and need a separately specified, authenticated callback). The mock serves it at `POST /token`;
  the real server's token endpoint is its own (AlgorandGoogleDriveAccount#28):

```http
POST /token
Authorization: Basic <client credentials>
Content-Type: application/x-www-form-urlencoded

grant_type=urn:openid:params:grant-type:ciba&auth_req_id=ciba_8257c10231dd9b46e2f877484cf0b2a3&resource=https%3A%2F%2Fmcp.biatec.io%2Ftools
```

Pending: `400 {"error":"authorization_pending","interval":2}`; too fast: `400 {"error":"slow_down","interval":7}` (+5 s each time); denied: `400 {"error":"access_denied"}`
(`"reason":"code_failed"` after three wrong codes); expired: `400 {"error":"expired_token"}`. Success (`schemas/token-response.schema.json`: never a refresh or ID token):

```json
{
  "access_token": "<acr=biatec:consent, same scope and audience as challenged, bound to the group hash (claim consent_hash) and to consent_auth_req_id>",
  "token_type": "Bearer",
  "expires_in": 300,
  "scope": "sign",
  "consent_receipt": {
    "version": "1.1",
    "signed": {
      "authorization_details": [ { "type": "biatec_transaction", "...": "see section 2" } ],
      "auth_req_id": "ciba_8257c10231dd9b46e2f877484cf0b2a3",
      "client_fingerprint": "68dcf7ce89a943b0",
      "binding_code": "120934",
      "decision": "approve",
      "expiry": 1792425900,
      "sub": "<the requesting client's pairwise sub>",
      "binding_message": ""
    },
    "alg": "ES256",
    "signature": "<base64url r||s over JCS(signed)>",
    "issued_at": 1792425620,
    "device": {
      "kid": "<device key id>",
      "key_use": "approve",
      "public_key": { "kty": "EC", "crv": "P-256", "x": "...", "y": "..." },
      "attestation": "<compact JWS minted by Biatec OIDC: sub, device_kid, device_jwk_thumbprint, biatec_key_use>"
    }
  }
}
```

An approval carries a ninth signed member `remember` (`{ "applications": [<ids>] }`) when the user trusted applications on the card. The signature covers the RFC 8785 canonical JSON of the `signed` object (the eight members above, nine with `remember`; see the specification, section 8). A denial returns `access_denied` and the signed denial receipt is available at the endpoint in
section 4. The elevated token is for the bound group only and expires in 300 s from issue, so the backend retries the wallet call immediately
after the grant succeeds; if it still gets a 401 it starts a new request instead of reusing the consent.

## 2. `authorization_details` types

- **Purpose:** what the phone shows and what the receipt signs. **Fallback:** none. Built by OIDC from the decoded group or request
  body; client-supplied amounts or payees are never accepted.

```json
{
  "type": "biatec_transaction",
  "network": "algorand-testnet",
  "group_hash": "BJPMYYUBSMQWYXMEBZT2ZHFZTWCIJAFAIMUZEGOJZKJPRBKU5KNA",
  "sender": "ADDR...",
  "transfers": [
    { "receiver": "POOLADDR...", "asset": { "id": "31566704", "symbol": "USDC", "decimals": 6 }, "amount": "5000.000000", "fiat_value": { "amount": "4612.50", "currency": "EUR" } }
  ],
  "applications": [ { "id": 456, "trusted": true } ],
  "value_known": true,
  "fiat_value": { "amount": "4612.50", "currency": "EUR" },
  "origin": "https://dex.biatec.io"
}
```

```json
{
  "type": "biatec_limits_change",
  "current_limits": { "per_transaction_eur": "1000", "per_day_eur": "3000" },
  "requested_limits": { "per_transaction_eur": "2000", "per_day_eur": "5000" },
  "request_hash": "<lowercase hex SHA-256 of the RFC 8785 canonical request body>"
}
```

(`schemas/authorization-details-transaction.schema.json` and `authorization-details-limits-change.schema.json` are normative; the 1.0 shape with a `sha256:` hash, `transactions[]` and a
single `value_eur` is gone.) `transfers` lists every payment and asset transfer of the sender, in group order, at most 16; `applications` lists every application the group calls, at most 16, with
`trusted` true only when the user trusted it for this client on their own phone; `value_known` is false when a transfer could not be priced or an application is not trusted, and then there is no
`fiat_value`. A group that sends value where `transfers` cannot name it (a close-to or clawback address, a rekey, a transaction type other than payment, asset transfer and application call) is
refused with `403 consent_unsupported_operation`. A limits change carries no `origin`.

`group_hash` is the Algorand group id recomputed by Biatec OIDC from the handed transactions: base32, 52 characters; for one ungrouped transaction its transaction id. `grp` in the signing request is
the same 32 bytes in base64. The wallet endpoint refuses a `grp` that differs from the recomputed id or transactions whose own group ids are inconsistent (`400 group_hash_mismatch`, section 8) and a
consent token whose hash differs (`403 consent_hash_mismatch`); the backend recomputes the id itself as defence in depth. For Bitcoin it is the intent hash derived from a PSBT carrying prevout data
(lowercase hex, 64 characters) and the transfers are the outputs; Bitcoin consent is not supported by the mock (`403 consent_unsupported_network`).

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

200 { "signedTransactions": ["<base64>", "..."], "valuedEur": "4612.50" }
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
  "auth_req_id": "ciba_8257c10231dd9b46e2f877484cf0b2a3",
  "binding_code": "483920",
  "expires_in": 300,
  "interval": 2
}
```

`expires_in` is the consent window, never longer than `maxConsentSeconds` (section 16) or the group's remaining validity. The
600-second Confirm gate in the fallback of section 16 bounds how long a built preview may be confirmed, a separate clock.

The backend then polls the CIBA token grant (section 1, authenticated as the confidential client, honouring `interval` and `slow_down`) and retries the same request with the elevated bearer. The wallet endpoint
accepts only a consent bound to the hash of what it is asked to do, and consumes it only if signing proceeds (it **reserves** the consent, signs, then marks it spent; a handled failure releases it).
The mock returns this exact body so the backend is tested against the real shape. The mock also accepts `unsigned_group` for `transactions`.

**Everything the wallet endpoint can answer besides a signature** (AlgorandGoogleDriveAccount#28; each has a conformance case in `packages/consent-protocol/conformance/tests/refusals.conformance.test.ts`):

| Status and `error` | Meaning | `reason` or extra members |
| --- | --- | --- |
| `401 insufficient_user_authentication` with `auth_req_id` | Phone approval is needed (the challenge above) | `binding_code`, `expires_in`, `interval` |
| `401 insufficient_user_authentication`, `error_description: "consent not usable"`, no `auth_req_id` | The elevated token's consent is missing, expired, reserved, spent, for another network, sender, client or account | none: the client starts over with its ordinary token |
| `400 group_hash_mismatch` | `grp` or the group ids of the transactions do not match the handed transactions; a partial group | |
| `403 consent_hash_mismatch` | The elevated token is bound to a different group; the consent stays usable for the right one | |
| `403 consent_unsupported_operation` | Rekey, close-to, clawback, another transaction type, multisig sender | |
| `403 consent_unsupported_network` | The network has no consent support and the action needs consent | |
| `403 consent_unavailable` | The action needs the phone and the user has no active device (rule 8) | `reason: "no_active_device"` |
| `403 client_paused` | The user paused this client (rule 10) | `paused_until` |
| `403 unproven_client_blocked` | The user blocks clients without a proven domain | |
| `403 daily_cap_exceeded` | No active device, first day with the client: the reduced cap (100 EUR) would be exceeded, or the value is unknown | `cap_eur` (decimal string), `reason: "no_active_device"` |
| `429 too_many_pending_requests` | Three pending requests of this client for this user from this origin | `limit`, `scope: { client, origin }` |
| `429 rate_limited` | More than 60 new requests an hour for this client and user (anti-spam only) | `Retry-After` header |

The backend maps these to the user-facing codes `no_active_device`, `no_device_daily_cap`, `client_paused`, `unproven_client_blocked`, `consent_unsupported` and `too_many_pending` (T-240); the two 429s keep "try later".

## 4. Device enrollment, attestation, recovery, and receipts

- **Purpose:** authenticator support and evidence. **Fallback:** none.
- Enrollment (authenticator to OIDC, user authenticated by a fresh full login): the device's public keys (an approval key bound to the user's biometrics and a presence key, each attested with its own
  `biatec_key_use`, row 4), the platform attestation (row 25) and, optionally, an Apple wake token. There is no stored Firebase installation id any more. Devices become active after an activation
  delay, or at once when an active device signs a `biatec_device_approval` (specification 8.2 and 8.3, implemented by the authenticator in T-241 and T-242); a recovery code is issued once, after
  activation (not in the enrollment response). `active_at` is epoch seconds and `device_token` authenticates later `Authorization: Device <token>` calls. AlgorandGoogleDriveAccount#31 (enrollment
  hardening) and #32 (two keys) implement it.

```http
POST /devices
{ "kid": "biatec-authenticator-ab12", "public_key": "<jwk>", "attestation_challenge": "<nonce>", "platform_attestation": { "kind": "app_attest", "token": "<b64>", "key_id": "..." }, "name": "Pixel 9" }

201 { "kid": "biatec-authenticator-ab12", "active": false, "active_at": 1792486800, "attestation": "<jws>", "device_token": "<opaque>" }
```

  The mock enrolls one approval key, and a presence key when `presence_kid` and `presence_public_key` are sent; its seeded devices have both. The enrollment proof and the pairing code are T-242's and are not modelled.

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
  - `POST /consent/requests/{auth_req_id}/receipt` with the signed receipt envelope of section 1 (approve, deny and code_failed are all
    signed; deny and code_failed may use either key, an approval only the approval key). Answers: `200 { "status": "approved" | "denied" | "code_failed" }`; wrong code
    `400 { "error": "wrong_code", "attempts_remaining": n }`; the third wrong code, or any decision after it other than the
    `code_failed` receipt, `403 { "error": "access_denied", "reason": "code_failed" }`; already decided `409`; expired `410`;
    inactive device `403`.
  - The attestation JWS returned by `POST /devices` is stored by the device and sent back in every receipt.
  - The device channel (AlgorandGoogleDriveAccount#29, row 24 of the table at the top; the app side is T-244): the authenticator holds a SignalR connection to `/hubs/device` (WebSockets only,
    on Biatec OIDC's own cluster, Redis backplane) while it is in the foreground, and on Android also while its opt-in "Stay connected" service runs. It authenticates with a single-use ticket from
    `POST /devices/hub-ticket` (`Authorization: Device <token>` plus the device proof), never the device token in a URL. The server sends **hints only**: `consent_request` `{ "auth_req_id" }`,
    `consent_closed` `{ "auth_req_id" }`, `device_enrolled` `{ "kid" }` and `settings_changed` `{}`; never an amount, asset, address, client, code or user identifier. The app fetches the request
    with its device token as above. Without a platform push service nothing wakes an app that is not running: an optional Apple wake sender (off unless the owner configures it) is the only
    wake mechanism on iOS, and `PUT /devices/push` with `{ "provider": "apns", "token": "..." }` registers its target (re-sent at most every 10 minutes, T-053). `fcm` and the Firebase
    installation id are gone from the real server. The app also polls every 5 seconds while its Approvals screen is open.
- Delivery in the mock: when a consent request opens, the mock records one message per active device that has a push token, whose data is `{ "auth_req_id": "<id>" }` and nothing else
  (`GET /mock/push-outbox`); it accepts `provider` `apns` and refuses `fcm` with `400 unsupported_push_provider`. The mock also hosts the device channel: `POST /devices/hub-ticket` (single use, 30 seconds) and the SignalR JSON hub at `/hubs/device`, which sends the four hints above (see the mock README). The visible text of a platform notification is a fixed, localizable string with no amount,
  asset, address or client name.
- Receipt retrieval, by the client that opened the request (direct entry) or whose token was challenged (step-up):

```http
GET /consent/receipts/ar_8f2c...
Authorization: Bearer <client token>

200 <same object as consent_receipt in section 1>
```

A step-up receipt has `binding_message: ""`; a direct-entry receipt carries the text only when the client's domain is proven.
Receipts verify offline against the device public key and attestation (specification 8.1). The backend verifies and stores the receipt (T-238). A receipt that approves must be signed by an approval key:
the posted receipt is protocol 1.1, and a 1.0 receipt is refused (`400 invalid_receipt`).

## 5. Algo Safe application-call valuation (changed by protocol 1.1)

- **Purpose:** threshold on safe approvals. **No operator valuation any more.** Biatec OIDC keeps no list of applications and does not read a safe's boxes to value a proposal. An application call
  is listed in `applications` with `trusted: false` until the user trusted that application for this client on their own phone (the "trust" tick on the approval card, signed into the receipt as
  `remember`, or the settings screen). While any application is untrusted the group's `value_known` is false ("Value unknown", warning style) and the phone is asked whatever the amount; once the
  user trusted it, the group is valued at its transfers (a payment funding a safe counts, an `approveProposal` or `executeProposal` call counts zero). Implemented by AlgorandGoogleDriveAccount#26
  (the description) and #27 (the user's trusted list).
- **Fallback:** none needed; the effect (the phone is asked) is the old fallback. The mock models the same: an untrusted application makes the value unknown, `remember` trusts it.
- **Proposer's approval (found in T-033):** the deployed algo-safe contract records the proposer's own approval inside `proposeTransactionGroup`, so the proposer never sends a separate
  `approveProposal` (it would be refused as "already approved"). The spec's "propose, then approve as a second call" does not exist on chain. The chat card says that proposing records the
  proposer's approval; the phone shows the proposal's transfers only when the group itself carries them.

## 6. Biatec DEX pool call decoding (not part of protocol 1.1)

- **Purpose:** pool facts on the phone card. **Status:** not in protocol 1.1: `authorization_details` has no `pool` object. The card shows the decoded transfers and the application id.
  Reading a pool's fee tier and range is a possible later extension, not a row Biatec OIDC has to ship.

## 7. Test networks: no operator price table (changed by protocol 1.1)

- **Purpose:** thresholds fire in the testnet demo and CI. There is no operator price table (the `TestnetPrices` configuration proposed earlier is withdrawn): a test network has no market
  price, so its transfers are unpriced (`value_known` false) and the policy asks the phone for them unless the user turned **`ask_on_test_networks`** off on their phone (default on;
  `GET /consent/settings`, section 20). Every other reason to ask still applies. AlgorandGoogleDriveAccount#26 (unpriced) and #27 (the setting).
- **Fallback:** demo identities use "always ask" for the Biatec Chat client and no spending limits. The mock prices ALGO (0.20 EUR) and USDC (0.9225 EUR) with a static table of its own on both
  networks, so CI crosses the 1000 EUR threshold with a plain transfer; that table is a mock simplification, not a requirement.

## 8. Group-hash binding in the signing endpoint

- **Purpose:** "one byte different, refused". **Fallback:** none; the protocol is unsafe without it.
- Algorand: recompute the group ID from the handed transactions (`group_hash`, section 2) and refuse a mismatching `grp`, a group whose transactions carry a different group id, and more than one
  transaction with no group id. Bitcoin: derive the intent hash
  from a PSBT with prevout data. Compare either with the consented hash before signing.

```http
400 Bad Request
{ "error": "group_hash_mismatch", "error_description": "grp does not match the handed transactions" }

403 Forbidden
{ "error": "consent_hash_mismatch", "error_description": "elevated token is bound to a different group" }
```

Implemented by AlgorandGoogleDriveAccount#26 (the id) and #28 (the binding); the mock does both with `algosdk` and the conformance kit changes one byte after approval to prove it.

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
  and refuses with HTTP 403 `{ "error": "daily_cap_exceeded", "cap_eur": "100.00", "reason": "no_active_device" }` (an unknown value never fits under the cap). The window is the client's first
  24 hours and the user's first day with the client; outside it a user with no device is refused only what needs the phone (`403 consent_unavailable`, `reason: "no_active_device"`). AlgorandGoogleDriveAccount#27 (the policy) and #28 (the refusal).
- Backend fallback (T-061, `backend/Biatec.Chat.Tools/Caps/NoDevice/`): before every signing request the confirm path reads
  `GET /consent/settings` (row 20). When it answers `device_active: false` (an enrolled device still in its activation delay
  counts as none):
  - an action that would need the phone (value above `threshold_eur` as the user set it, with no ceiling: it may be above 1000 or 0, and the tenant's default threshold (`consentThresholdCeilingEur`,
    now meaning "the default threshold") is used only when the settings endpoint answers 404, and as the floor of the soft cap; a value the backend cannot predict; `always_ask`; a preview's own `requireConsent`; or a soft or safe cap
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
  - If `GET /consent/settings` is missing or fails (a 404 means the endpoint is absent or enforcement does not apply to the client), nothing changes (OIDC's step-up is relied on). An answer is cached per
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

`"0.00"` for zero-valued calls and `null` when the value is unknown (an unpriced asset, an untrusted application, a network that cannot be priced); the backend then falls back and records it.
AlgorandGoogleDriveAccount#28 adds the member; two decimals, rounded half up, one helper everywhere.

## 12. Client identity for consent: fingerprint and proven domain (changed by protocol 1.1)

- **Purpose:** impostor protection without Biatec OIDC vouching for anyone. **No verified flag, no operator-set display name, logo or entity id, no federation chain.** The consent request's `client`
  object is `{ "fingerprint", "domain_proven", "domain"?, "linked" }`:

```json
{ "fingerprint": "68dcf7ce89a943b0", "domain_proven": true, "domain": "chat.biatec.io", "linked": true }
```

  `fingerprint` is the first 8 bytes of the SHA-256 of the `client_id` (16 lowercase hex characters, shown `xxxx-xxxx-xxxx-xxxx`); `domain` is present exactly when `domain_proven`; `linked` says whether
  this user already approved a request of the client on their phone. A client proves its domain by serving `https://<host of its client_uri>/.well-known/biatec-client.json` with
  `{ "client_ids": ["<client_id>"] }`; Biatec OIDC fetches it (https only, no redirects, no private addresses, cached and re-checked daily). The card shows the proven domain, or "Unproven application"
  with the fingerprint in the warning style, never a name. Only a client with a proven domain gets a rendered `binding_message`; others sign the empty string. Whether to trust a client is the
  user's decision (they link it by approving, may block unproven clients, may pause one). Implemented by AlgorandGoogleDriveAccount#25.
- **Fallback in use:** the Biatec Chat backend answers `GET /.well-known/biatec-client.json` for the tenant bound to the request host with `{ "client_ids": ["<the tenant's OIDC client id>"] }`
  (`Content-Type: application/json`, `Cache-Control: public, max-age=3600`, `404` for an unknown host), so the proof works the day Biatec OIDC checks it; until then the card says "Unproven application".

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

- **Purpose:** consent cannot outlive the group. **Fallback:** rely on the 600-second Confirm gate and rebuild more often. AlgorandGoogleDriveAccount#28.
- Request field (see the request in section 3). OIDC sets the CIBA `expires_in` to the minimum of `maxConsentSeconds`, its own
  default and the group's remaining validity. A value below a minimum (for example 30) is refused with HTTP 400
  `{ "error": "invalid_request", "error_description": "maxConsentSeconds too small" }`.

## 17. Origin-aware consent requests

- **Purpose:** one compromised Biatec site cannot block or pause the others. **Fallback:** slots and pauses apply per client for
  all Biatec sites together.
- The backend sends the page origin, validated against the tenant's allowed origins and never raw client input, in the `origin` field
  of the signing request (`origin` on CIBA direct entry, a canonical origin: scheme, lower-case host, optional non-default port). OIDC scopes the three-pending-request limit, rate limits and
  24-hour pauses by client and origin, and signs the origin in `authorization_details.origin` (labelled "as reported by the client" on the card: only a proven domain says who the client is).
  AlgorandGoogleDriveAccount#28 (the request) and #30 (the pause).

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
- Requirement: raising the threshold (the user's own, with no ceiling), removing "always ask", relaxing per-client settings, trusting an application, turning a protection off and lifting a pause
  require a statement signed by the **approval key of an active device** (`biatec_settings_change`, specification 8.2), never a web session alone. Tightening needs no consent. The statement is
  checked by `verifyDeviceSigned` in `scripts/lib.mjs`: signature over `JCS(signed)`, the attested `biatec_key_use` is `approve`, `issued_at` within 300 seconds, a single-use nonce of 16 to 128
  base64url characters. A loosening attempt through the web is not a retryable wallet call: the backend does not poll for it and the chat reports "change this on your phone".
  AlgorandGoogleDriveAccount#30.

```http
403 Forbidden
{ "error": "insufficient_user_authentication", "acr_values": "biatec:consent", "reason": "consent_required" }
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
  "default_threshold_eur": "1000.00",
  "ask_on_test_networks": true,
  "device_enrolled": true,
  "device_active": true,
  "client": {
    "linked": true, "always_ask": false, "paused_until": null, "cooling_off_until": null, "first_use": false,
    "per_client_threshold_eur": null, "domain_proven": true,
    "trusted_applications": [ { "network": "algorand-mainnet", "app_id": 1234567 } ]
  }
}
```

The response is for the calling client and its account. `threshold_eur` is the user's own: no ceiling, it can be above 1000 or `"0.00"` (ask for everything); `default_threshold_eur` is what an account with
no settings uses (a deployment's default, 1000.00). `paused_until` follows the `origin` query parameter when given, else the client's latest pause. A consent token is refused (`401`); `404` means the
endpoint is absent or consent enforcement does not apply to the client, and the backend then keeps its own fallback. The backend's prediction adds its own triggers (soft cap, safe caps via
`requireConsent`) and never reports less consent than OIDC would require. If the call fails, the fallback card text is used. AlgorandGoogleDriveAccount#27.

## 21. Device list and cancelling a pending enrollment (T-084)

- **Biatec OIDC issue:** AlgorandGoogleDriveAccount#30.
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

- **Biatec OIDC issue:** AlgorandGoogleDriveAccount#30. The members below are the mock's current subset (T-084); the 1.1 statement of specification 8.2 adds `block_unproven_clients` (the name
  used here before, `block_unverified_clients`, is replaced), `ask_on_test_networks`, `trusted_applications_add` / `_remove`, `label`, `paused` with its `origin`, and `unlink`; the settings
  screens adopt them in T-245.
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

## 24. Minimum authenticator build (T-232)

- **Purpose:** MASVS-CODE-2: an authenticator that is too old to be safe stops opening the secure area and asks the user to update, without
  waiting for a store to remove it.
- **Biatec OIDC issue:** AlgorandGoogleDriveAccount#30.
- **Needed:** Biatec OIDC sends the lowest accepted build number of the authenticator on **every** answer to a device call, errors
  included, as a response header:

```
X-Biatec-Min-App-Build: 7
```

  A whole number from 0 to 1,000,000, the number after the plus sign of the app's `version` (`0.1.0+7`). Biatec OIDC raises it when a
  release fixes a vulnerability and lowers it never (the app keeps the highest it has seen, so a later lower answer does not reopen an
  old build). The header name and the meaning are proposals for the OIDC owners to confirm.
- **Fallback in use:** an optional expiry date compiled into the build, `--dart-define=BUILD_EXPIRES=2027-06-30` (UTC): after it the app opens
  nothing and shows the same update screen ("reached the end of its time"). Without the header and without a date an old build keeps running;
  the release checklist therefore sets a date on every production build.
- **Mock:** `X-Biatec-Min-App-Build` on every answer, from `MOCK_MIN_APP_BUILD` (default 1) or the `minAppBuild` option; covered by
  `mock-oidc/test/mock-oidc.test.ts`. The authenticator's tests cover the rest (`apps/biatec_authenticator/test/update/`).

## 25. Platform attestation at enrollment (T-234)

- **Purpose:** MASVS-RESILIENCE-1 and -2: Biatec OIDC learns that an enrollment comes from the genuine app on a genuine device, and can refuse
  or flag one that does not.
- **Biatec OIDC issue:** AlgorandGoogleDriveAccount#36 (verification of Play Integrity, App Attest and Android key attestation), with the challenge endpoint of #31.
- **Needed:**
  1. `POST /devices/challenge` with `Authorization: Bearer <fresh login token>` (enrollment) or `Authorization: Device <token>` (refresh) returns a
     single-use nonce bound to the user:

```json
{ "nonce": "Yk5vcmNlLXNvbWV0aGluZy1yYW5kb20", "expires_in": 300 }
```

  2. `POST /devices` takes two more fields, sent together or not at all:

```json
{
  "kid": "biatec-authenticator-…",
  "public_key": { "kty": "EC", "crv": "P-256", "x": "…", "y": "…" },
  "attestation_challenge": "Yk5vcmNlLXNvbWV0aGluZy1yYW5kb20",
  "platform_attestation": { "kind": "play_integrity", "token": "<integrity token>", "key_id": null }
}
```

     `kind` is `play_integrity` or `app_attest` (`key_id` is the App Attest key id). For App Attest the client data hash is
     `SHA-256(nonce)`; for Play Integrity the request nonce is `base64url(SHA-256(nonce))` without padding. Biatec OIDC verifies the token
     with Google or Apple, checks that the nonce is the one it issued to this user and spends it (also when the rest fails), and stores the result
     with the device. The answer carries `attestation_refresh_after` (epoch seconds) when an attestation was accepted.
  3. `PUT /devices/attestation` (device token) sends a fresh `attestation_challenge` and `platform_attestation` and answers
     `{ "kid": "…", "attestation_refresh_after": 1795017600 }`. The app does it when the date has passed (at the next refresh, on resume).
- **Fallback in use:** enrollment without an attestation: the authenticator sends none (never a made-up one) when the platform cannot attest or
  Biatec OIDC offers no challenge, and Biatec OIDC decides what an unattested device may do. A device enrolled that way is never asked to refresh.
- **Build:** iOS needs the App Attest capability (`Runner.entitlements`); Android needs the release's Google Cloud project number,
  `-PplayIntegrityProject=<number>` or `gradle.properties` (without it the attestation is not supported there).
- **Mock:** all three endpoints; it checks the shape of the token, spends the nonce once, binds it to the user, expires it after 300 s and
  refuses a replay (`mock-oidc/test/mock-oidc.test.ts`); it cannot check a token against Google or Apple.

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
- **Widget status (T-235):** the prerequisite is done. The widget no longer reads `popup.closed`; while it waits it shows a Cancel button in the header, and the status poll, the timeout and Cancel are the only ways a wait ends. The header can be switched on once this widget version is released and hosts have taken it (a popup the user closes by hand now ends the wait through Cancel or the timeout).
- **Fallback in use:** none; the exposure needs a compromised Biatec OIDC (or backend) page, which is why the finding is rated Low.

## Proof of possession on device calls (BCHAT-030)

- **Biatec OIDC issue:** AlgorandGoogleDriveAccount#29 (the device channel's ticket call needs the proof), #30 (device calls) and #32 (the presence key signs it).
- **Needed:** every call that authenticates with `Authorization: Device <token>` (the device and settings API, `GET /devices/requests`, `GET /consent/requests/{id}`, `POST .../receipt`, `PUT /devices/push`) also carries `X-Device-Proof`, and Biatec OIDC refuses it without a valid one. The device token alone is a long-lived bearer: a copy of it lets anyone list the user's requests and re-point the push target.
- **Shape (DPoP-style, RFC 9449):** a compact JWS, `alg` ES256, `typ` `biatec-device-proof+jwt`, `kid` = the device's kid, signed by the enrolled device key (raw r||s). Claims: `htm` (method), `htu` (the URL the client calls), `iat` (seconds), `jti` (unique, 16 to 128 characters) and `ath` (base64url SHA-256 of the device token). The server checks the signature against the device's public key, `htm` and `htu` against the request, `ath` against the presented token, `|now - iat| <= 60`, and remembers the `jti` for twice that window (a replay is refused). A refusal is `401` with `WWW-Authenticate: Device error="invalid_proof"`.
- **Also needed:** `PUT /devices/push` is rate limited per device (the mock allows 6 a minute, `429` beyond).
- **Fallback in use:** the authenticator already sends the proof (`lib/security/device_proof.dart`); the mock checks it when started with `MOCK_DEVICE_PROOF=1` (`deviceProof: true`) and enforces the push limit always. Against the real server the bearer alone is still what is accepted, so this stays a request until Biatec OIDC implements it. The mock's default and the conformance kit still use the bearer alone.
- **Interaction with BCHAT-007:** the approval key is gated by the user's biometrics, so signing a proof per call with it would prompt each time. Protocol 1.1 settles it: the proof, `deny` and
  `code_failed` receipts are signed by the **presence key** (usable while the device is unlocked); only approvals, settings changes and the approval of a new device use the approval key
  (row 4, #32). The mock's seeded devices have both keys; proof mode (`MOCK_DEVICE_PROOF=1`) still checks the proof against the approval key until T-241 moves it.

