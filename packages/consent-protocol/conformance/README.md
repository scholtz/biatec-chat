# Consent conformance suite

Self-test for anything that implements the Biatec Consent Protocol (`../spec/biatec-consent-protocol.md`): a Biatec
OIDC stand-in, a test double in your backend, or your own authenticator integration. It runs both entries and checks
that the receipts they produce share one schema, one canonical form and the same signing rules. It does not certify
the real Biatec OIDC.

```
pnpm --filter @biatec/consent-protocol conformance                                     # against the bundled mock
CONFORMANCE_OIDC_URL=https://oidc.example.test pnpm --filter @biatec/consent-protocol conformance   # against your server
```

With no `CONFORMANCE_OIDC_URL` the suite starts the mock OIDC consent server (`../mock-oidc`) on a random local port (`CONFORMANCE_BASE_URL`
is the older name of the variable and still works).

## Against a real server (stage)

The kit takes everything from environment variables in your own shell and writes nothing to disk but the report. A case declares what it
needs, and a case whose needs this run cannot offer is reported as **skipped with the reason**, never as passed:

| Need | Available when |
| --- | --- |
| `mockHelpers` | the in-process mock, or a standalone mock that offers `/mock/*` and was announced with `CONFORMANCE_MOCK_HELPERS=1` (the flag only says the helpers exist: it supplies no secret, token or key). Never against a real server, so every case that plays the authenticator (the receipt cases) is skipped there: a real receipt needs a real phone. |
| `clientSecret` | `CONFORMANCE_CLIENT_SECRET` is set (direct entry) |
| `userToken` | `CONFORMANCE_USER_TOKEN` is set (the access token of the client and user under test; `CONFORMANCE_STEPUP_BEARER` is the older name) |
| `deviceKey` | `CONFORMANCE_DEVICE_PRIVATE_JWK` and `CONFORMANCE_DEVICE_TOKEN` are set |
| `settingsUser` | `CONFORMANCE_SETTINGS_BEARER`, `CONFORMANCE_SETTINGS_DEVICE_TOKEN` and `CONFORMANCE_SETTINGS_DEVICE_KID` are set: an account of its own with an active device (its approval key is `CONFORMANCE_DEVICE_PRIVATE_JWK`) and the verified client linked. The settings cases change its threshold, trust and links and put them back, so no other case may use it (the mock's `hank@example.com`, tokens `hank-chat-sign` and `device-token-hank`, `hank-device`). |
| `relyingParties` | the in-process mock, or `CONFORMANCE_RP_REGISTRATION=1`: the server may be asked to register throwaway relying parties (`POST /register`, `private_key_jwt`; the key pairs are generated in memory and no secret is involved, but the clients stay registered at that server, so this is opt-in) |
| `presenceKey` | `CONFORMANCE_PRESENCE_PRIVATE_JWK` is set: the private presence key of the device, for the cases that prove a presence key never approves (a link or a relying party's request) |
| `bearer:<name>` | the matching `CONFORMANCE_BEARER_*` variable is set |

```
export CONFORMANCE_OIDC_URL=https://oidc.stage.example CONFORMANCE_ISSUER=https://oidc.stage.example CONFORMANCE_CLIENT_ID=... CONFORMANCE_ADDRESS=...
read -rs CONFORMANCE_CLIENT_SECRET; read -rs CONFORMANCE_USER_TOKEN; export CONFORMANCE_CLIENT_SECRET CONFORMANCE_USER_TOKEN   # typed, not written anywhere
pnpm --filter @biatec/consent-protocol conformance
```

Seed users, tokens and the vector device key are defaults only for the in-process mock. Against any server named by `CONFORMANCE_OIDC_URL`, `CONFORMANCE_MOCK_HELPERS=1` included, an unset secret stays empty and the cases that need it are skipped with the reason. `tests/report.test.ts` is excluded whenever `CONFORMANCE_OIDC_URL` is set, so its cases never count as results of that server; its nested run starts from a clean environment. The cases that open a dozen or two hundred
requests (`too_many_pending_requests`, `rate_limited`) need `mockHelpers` so that they never flood a real person's phone.

`report/run-<time>-<host>.json` is written for every run: the server URL (without credentials or query), the commit of this repository, and
pass, fail and skip per case with the reason. Supplied secrets, `Bearer`/`Basic`/`Device` values, JWTs, binding codes and auth request ids are
redacted (`src/needs.ts`, asserted by `tests/report.test.ts`). The console output of vitest is not redacted: do not paste it into a ticket.
Whenever `CONFORMANCE_OIDC_URL` is set the vitest JSON and JUnit files are not written, because they hold raw failure messages. The report also records `runState` (completed, aborted or cancelled), marks cases that never ran as `not_run` (not `skipped`) and lists `dirtyPaths` of this package that differ from the commit.

## What it checks

For the **step-up entry** (`POST /wallet/{network}/{address}/sign` answers 401 `insufficient_user_authentication`) and
the **direct entry** (`POST /connect/ciba`) the suite plays client and authenticator:

1. the entry response matches `stepup-challenge` / `backchannel-authentication-response`;
2. the device fetches the consent request (matches `consent-request`, never contains the binding code);
3. the device signs and posts a receipt; the client redeems it at `POST /token` (matches `token-response`);
4. the returned `consent_receipt` is checked by `receiptProblems`:
   - it matches `consent-receipt` (so the same members for both entries),
   - `signed` is in RFC 8785 canonical form and the vectors in `../vectors/canonicalization.json` canonicalize identically,
   - the ES256 device signature over `JCS(signed)` and the Biatec OIDC attestation verify against the published JWKS,
   - `signed` restates the consent request member for member, with the entered binding code and decision `approve`;
5. the consent token signs the group once, and a second use is refused;
6. the two entries produce receipts with identical structure.

`tests/refusals.conformance.test.ts` covers the answers of the signing endpoint besides a signature and the policy behind them: the group id
recomputed (`400 group_hash_mismatch`), `403 consent_unsupported_operation` (rekey, close-to, clawback), `consent_unsupported_network`,
`consent_unavailable` with `reason: no_active_device`, `client_paused`, `unproven_client_blocked`, `daily_cap_exceeded` with `cap_eur`,
`consent_hash_mismatch` (one byte changed after approval, the consent stays usable), `401 consent not usable` on a second use, the consent
token refused on another endpoint, `valuedEur`, `429 too_many_pending_requests`, `429 rate_limited`, the user's own threshold (5000 EUR) and
`GET /consent/settings`. The receipts are protocol 1.1 (`client_fingerprint`, `device.key_use` approve) and every group is a real unsigned Algorand group.

`tests/settings.conformance.test.ts` covers the settings of Biatec OIDC issue 30 (the mock README, "Settings", has the shapes): tightening without and loosening with the approval key's `biatec_settings_change` (`403 insufficient_user_authentication` with `reason: consent_required` otherwise; replay, other change, other account, presence key and stale statements refused), the threshold with no ceiling and a 2000 EUR transfer signed under 5000, `ask_on_test_networks`, `block_unproven_clients`, a display-safe `label`, trusted applications (add loosens, remove tightens, the policy follows), `unlink` (first use again) and `remember` on the receipt. Each needs `settingsUser`, `deviceKey` and `mockHelpers`.

`tests/relying-party.conformance.test.ts` covers protocol 1.2 (spec chapter 14, Biatec OIDC issue 38; the mock README, "Relying parties", has the shapes). The kit plays a bank (a `private_key_jwt` key pair in memory) and the phone (the device of `CONFORMANCE_DEVICE_*`; the domain proof goes through the mock's `POST /mock/serve`). Registration and its refusals (a secret method, extra grants, redirect URIs, wallet scopes, a private key in `jwks`, `client_uri` rules; `relyingParties`); the client assertion (ES256 and PS256, replay, audience, expiry, a lifetime over 5 minutes, `iss` and `sub`, another key, `none`, HS256; a relying party never obtains `sign` or `manage-limits`; `relyingParties`); linking (the three schemas, the link code, `verifyLinkPinned` and `verifyLink`, no proven domain gives `domain: null`, a wrong domain, three wrong codes, a stale statement, a replay, a presence key, deny, only the creator reads the link, the text rules of `user_label`); the payment, login and action requests (approved on the phone, redeemed, scope `openid` only, the receipt through `verifyReceiptPinned` with the pinned keys and through `verifyReceipt` with the JWKS, the same receipt at `GET /consent/receipts/{id}` and at `GET /consent/requests/{id}/receipt`, one changed member refused, the token refused at every other endpoint and single use); every `400 invalid_authorization_details` (the text rules for each string member, the IBAN's check digits, amounts, a date, unknown members, `origin` inside the object); an unlinked party and `unknown_user_id`; `DELETE` of the link and `GET /consent/links/subject/{subject}/devices`; a deny and three wrong codes; the receipt must restate the request. The cases that play the phone need `mockHelpers`, `deviceKey` and `relyingParties`; against a real server without them they are reported as skipped.

`tests/detection.test.ts` proves the suite fails on a bad receipt: it alters a signed member, the signature, the
device key, the binding code, the decision and the schema, and it runs both entries through a proxy that tampers with
the receipt on the wire.

## Pointing it at your implementation

Defaults are the mock's public seed data and apply **only to the in-process mock** (no `CONFORMANCE_OIDC_URL`); against any other server every secret, token and key comes from your environment. Override with environment variables:

| Variable | Meaning | Default |
| --- | --- | --- |
| `CONFORMANCE_OIDC_URL` | The main variable: server under test (unset starts the in-process mock). `CONFORMANCE_BASE_URL` is the old alias. | mock |
| `CONFORMANCE_ISSUER` | Expected `iss` of the device attestation | `https://oidc.biatec.test` |
| `CONFORMANCE_CLIENT_ID`, `CONFORMANCE_CLIENT_SECRET` | Verified client (HTTP Basic) for direct entry and token redemption | `biatec-chat` |
| `CONFORMANCE_LOGIN_HINT` | User for the direct entry | `alice@example.com` |
| `CONFORMANCE_STEPUP_BEARER` | Access token of that client and user, which triggers step-up for the group | `alice-chat-sign` |
| `CONFORMANCE_NETWORK`, `CONFORMANCE_ADDRESS`, `CONFORMANCE_ORIGIN` | Wallet network, sender address (a valid Algorand address), request origin | `algorand-testnet`, a fixed test address |
| `CONFORMANCE_UNSIGNED_GROUP` | JSON array of base64 unsigned Algorand transactions above the user's threshold (the refusal cases always build their own groups from `CONFORMANCE_ADDRESS`) | 5000 USDC from the address |
| `CONFORMANCE_BEARER_NO_DEVICE`, `_PAUSED`, `_CAPPED`, `_UNPROVEN_BLOCKED`, `_ALWAYS_ASK`, `_THRESHOLD_5000` | Access tokens of a user with no device, one who paused the client, one inside the first day without a device, one who blocks unproven clients (using a client without a proven domain), one who always asks, one whose threshold is 5000 EUR | the mock's `bob`, `dave`, `erin`, `gina`, `carol`, `frank` tokens |
| `CONFORMANCE_UNSUPPORTED_NETWORK` | A network the server cannot consent to | `bitcoin-testnet` |
| `CONFORMANCE_DEVICE_TOKEN`, `CONFORMANCE_DEVICE_KID`, `CONFORMANCE_DEVICE_PRIVATE_JWK` | Enrolled test device that signs receipts (private JWK as JSON) | vector device |
| `CONFORMANCE_PRESENCE_PRIVATE_JWK` | The same device's private presence key (the 1.2 cases that prove a presence key never approves) | vector presence key |
| `CONFORMANCE_RP_REGISTRATION` | `1` allows the relying-party cases to register throwaway clients at the server | in-process mock only |

`tests/report.test.ts` runs the kit against a stub server without helpers and asserts the skips and the absence of marker secrets from the report.

The authenticator side signs 1.1 receipts with the approval key and needs the device's attestation and public key, which the suite reads from the helper
`GET /mock/devices/{kid}` (a standalone mock serves it only when started with `MOCK_TEST_HELPERS=1`; point the kit at it with `CONFORMANCE_MOCK_HELPERS=1`). A server without that helper must offer an equivalent; adapt the lookup in `approve` in `src/kit.ts`.

## Reports

Every run writes `report/run-<time>-<host>.json` (see above), and against the in-process mock also `report/conformance.json` (vitest JSON) and `report/conformance.junit.xml`.
Attach them when you tell us your integration passes. `report/` is git-ignored.
