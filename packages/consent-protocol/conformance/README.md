# Consent conformance suite

Self-test for anything that implements the Biatec Consent Protocol (`../spec/biatec-consent-protocol.md`): a Biatec
OIDC stand-in, a test double in your backend, or your own authenticator integration. It runs both entries and checks
that the receipts they produce share one schema, one canonical form and the same signing rules. It does not certify
the real Biatec OIDC.

```
pnpm --filter consent-protocol conformance                                       # against the bundled mock
CONFORMANCE_BASE_URL=https://oidc.example.test pnpm --filter consent-protocol conformance   # against your server
```

With no `CONFORMANCE_BASE_URL` the suite starts the mock OIDC consent server (`../mock-oidc`) on a random local port.

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

`tests/detection.test.ts` proves the suite fails on a bad receipt: it alters a signed member, the signature, the
device key, the binding code, the decision and the schema, and it runs both entries through a proxy that tampers with
the receipt on the wire.

## Pointing it at your implementation

Defaults are the mock's public seed data. Override with environment variables:

| Variable | Meaning | Default |
| --- | --- | --- |
| `CONFORMANCE_BASE_URL` | Server under test (unset starts the mock) | mock |
| `CONFORMANCE_ISSUER` | Expected `iss` of the device attestation | `https://oidc.biatec.test` |
| `CONFORMANCE_CLIENT_ID`, `CONFORMANCE_CLIENT_SECRET` | Verified client (HTTP Basic) for direct entry and token redemption | `biatec-chat` |
| `CONFORMANCE_LOGIN_HINT` | User for the direct entry | `alice@example.com` |
| `CONFORMANCE_STEPUP_BEARER` | Access token of that client and user, which triggers step-up for the group | `alice-chat-sign` |
| `CONFORMANCE_NETWORK`, `CONFORMANCE_ADDRESS`, `CONFORMANCE_ORIGIN` | Wallet network, sender address, request origin | `algorand-testnet`, `ALICEADDRESS` |
| `CONFORMANCE_UNSIGNED_GROUP` | JSON array of unsigned group items above the user's threshold (the default is the mock's descriptor encoding, not msgpack) | mock descriptor |
| `CONFORMANCE_DEVICE_TOKEN`, `CONFORMANCE_DEVICE_KID`, `CONFORMANCE_DEVICE_PRIVATE_JWK` | Enrolled test device that signs receipts (private JWK as JSON) | vector device |

The authenticator side needs the device's attestation and public key, which the suite reads from the helper
`GET /mock/devices/{kid}` (a standalone mock serves it only when started with `MOCK_TEST_HELPERS=1`). A server without that helper must offer an equivalent; adapt the lookup in `approve` in `src/kit.ts`.

## Reports

Every run writes `report/conformance.json` (vitest JSON) and `report/conformance.junit.xml` next to the console output.
Attach them when you tell us your integration passes. `report/` is git-ignored.
