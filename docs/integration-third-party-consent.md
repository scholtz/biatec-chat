# Third-party integration with the Biatec Consent Protocol

For a bank, a fintech or any service that wants a user to approve one precise action on their Biatec Authenticator, without
asking Biatec for permission. The protocol text is [`packages/consent-protocol/spec/biatec-consent-protocol.md`](../packages/consent-protocol/spec/biatec-consent-protocol.md)
(draft, version 1); [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) wins on any conflict.

## Read this first: what you can run today

The consent endpoints belong to Biatec OIDC, which this repository cannot change. They do not exist there yet (the list of needed
changes and the fallback for each is in [required-changes-biatec-oidc.md](required-changes-biatec-oidc.md)). So today:

| What | Status |
| --- | --- |
| Specification, JSON Schemas, signed test vectors | Built: `packages/consent-protocol/{spec,schemas,vectors}` |
| Mock Biatec OIDC covering both entry points, receipts and enrollment | Built: `packages/consent-protocol/mock-oidc` |
| Conformance test suite that runs against a mock or a real endpoint | In progress (T-048). The package's own spec and vector tests run now |
| Real Biatec OIDC consent endpoints | Not available. Above-threshold signing cannot be enabled against real OIDC until they ship |
| Dynamic registration of confidential clients | Not available. An operator reviews and registers your client |
| Authenticator app | Scaffold only |

Build and test against the mock; switch the base URL when Biatec OIDC ships.

## The three steps

1. **Publish an OpenID Federation entity configuration** on your domain and obtain a subordinate statement from a trust anchor the
   deployment recognizes (initially Biatec's own). Without a chain to a trust anchor your client is shown to users as
   "Unverified application" with a fingerprint of its `client_id`, nothing else. Evaluation of trust chains is a Biatec OIDC change;
   until it ships only Biatec's own clients are pinned as verified.
2. **Register a client** with RFC 7591 Dynamic Client Registration, declaring `backchannel_token_delivery_mode` and the
   `authorization_details_types` you will use (`biatec_transaction`, `biatec_limits_change`). Today registration is operator
   reviewed. The `manage-limits` scope stays reserved for operator-allowlisted clients.
3. **Ask for consent**, by either entry point. Both produce the same request object and the same signed receipt.

### Entry A: direct entry

You already have the exact unsigned transaction group and want the consent in hand first.

1. `POST /connect/ciba` with the group and an optional `binding_message`. Biatec OIDC decodes the group itself and builds the
   `authorization_details`. Your amounts and payees are never what is approved; the group is.
2. Show the returned `binding_code` to the user on your surface. They type it into the authenticator; the authenticator never
   displays it (number matching).
3. Poll `POST /token` with `grant_type=urn:openid:params:grant-type:ciba` and the `auth_req_id` until you get the token
   (`authorization_pending` while waiting, `access_denied` on a denial, `expired_token` after 300 s). The response carries the signed
   `consent_receipt`.
4. Present the elevated token to `POST /wallet/{network}/{address}/sign` with the same group. A group that differs by one byte has a
   different hash and is refused.

### Entry B: step-up entry

1. Call `POST /wallet/{network}/{address}/sign` (or `PUT /wallet/limits`) with an ordinary token.
2. If consent is needed, Biatec OIDC answers `401 insufficient_user_authentication` with `acr_values: biatec:consent`, an
   `auth_req_id` and the `binding_code`. Show the code, poll the token endpoint as above, then retry the same request with the
   elevated token. The Biatec Chat backend uses this entry for everything, so it never has to guess the threshold.

The receipt can be fetched again at `GET /consent/receipts/{auth_req_id}` by the client the request belongs to.

## Rules your integration must respect

- Show the binding code only on your surface. A wrong code gets three attempts in total; the third wrong code is recorded as
  `code_failed`.
- A consent lasts 300 seconds, is bound to one group hash and is spent only when signing succeeds.
- Biatec OIDC signs for one sender address per call. An action that needs two senders is two groups, two calls, possibly two consents.
- First use of your client by a user, and the first 24 hours after you register (unless you chain to a trust anchor), always need the phone.
- A denial cancels your pending action. Do not retry in a loop: at most three requests may be pending per client per user, and
  after three denials in a day the user can pause your client for 24 hours. (The mock does not model these limits.)
- Verify every receipt offline before you trust it: the device signature over the RFC 8785 canonical object, the enrollment
  attestation, and the Biatec OIDC JWKS (section 8.1 of the spec). Never trust an embedded key that is not in the JWKS.

## Try it locally

```sh
pnpm install
pnpm --filter mock-oidc start                 # http://127.0.0.1:8089
pnpm --filter mock-oidc test                  # the mock's own tests
pnpm --filter @biatec/consent-protocol test   # schema and vector tests
```

The mock seeds public test data (clients `biatec-chat`, `bank-example`, `unverified-app`; users `alice@example.com`,
`carol@example.com` who always asks, and `bob@example.com` who has no device) and is documented in
[`mock-oidc/README.md`](../packages/consent-protocol/mock-oidc/README.md). Its simplifications matter: transaction groups are
`base64(JSON)` rather than msgpack, push is replaced by polling, and the per-origin pending-request limits are not modelled.

When the conformance suite lands it will take the base URL of an implementation and exercise both entries, asserting that their
receipts validate against the same schema, use the same canonical form and signing rules, and differ only in field values.
