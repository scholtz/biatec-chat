# Relying parties: confirming payments, logins and actions with Biatec Authenticator

For an organisation (a bank, a broker, an internal system) that wants its users to confirm payments, logins and other actions on
their Biatec Authenticator, without Biatec vouching for it. The normative text is chapter 14 "Relying parties" of
[`packages/consent-protocol/spec/biatec-consent-protocol.md`](../packages/consent-protocol/spec/biatec-consent-protocol.md)
(protocol 1.2); [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) ("Universal Authenticator") wins on any conflict.
The server side is Biatec OIDC issue 38, which is not built yet: today you build against the mock in
`packages/consent-protocol/mock-oidc` (see [required-changes-biatec-oidc.md](required-changes-biatec-oidc.md)).

| You need to know | Where |
| --- | --- |
| The trust model (four statements) | Spec 14.1 |
| Registering a `private_key_jwt` client, the well-known file `/.well-known/biatec-client.json` | Spec 14.2 |
| Linking a user: link code `biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>`, the six digit code, pinning the device keys | Spec 14.3 |
| Payment, login and action requests (`biatec_payment`, `biatec_login`, `biatec_action`) | Spec 3.3 to 3.5 and 14.4 |
| `details_hash` and a worked example | Spec 3.6 |
| Verifying a receipt with the pinned device key only | Spec 8.5 and 14.5, `verifyReceiptPinned` in `packages/consent-protocol/scripts/lib.mjs` |
| What a new phone of the user means for a pinned key | Spec 14.6 |
| Text rules for every string you send | Spec chapter 15 |
| Schemas and signed test vectors (`receipts-1.2.json`, `link.json`, `text-rules.json`) | `packages/consent-protocol/{schemas,vectors}` |

The reference helpers (`isValidIban`, `detailsHash`, `verifyReceiptPinned`, `verifyLinkPinned`, `isRelyingPartyText`) are in
`packages/consent-protocol/scripts/lib.mjs`; a relying party written in another language reproduces them from the spec and checks
itself against the vectors.
