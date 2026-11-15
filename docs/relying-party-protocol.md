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

**What the pinned key does and does not give you.** After you pinned a device key, Biatec OIDC cannot alter or forge the content of a receipt for that key, and a receipt signed by another key is detected. At link time the list of keys comes from the same server answer as the user's signed approval, so you trust the operator for which keys are the user's then; later changes of the list are your own policy decision (spec 14.1 and 14.6). `verifyReceiptPinned` needs your own `auth_req_id`, `sub`, client fingerprint, the code you displayed, the origin you reported (or `null`) and a Set of ids you already acted on; record the id after it returned true and before you execute anything. A request closed because the user (or you) removed the link ends with `access_denied`, `reason: "unlinked"` and no receipt: it is not a denial by the user.

The reference helpers (`isValidIban`, `detailsHash`, `verifyReceiptPinned`, `verifyLinkPinned`, `isRelyingPartyText`) are in
`packages/consent-protocol/scripts/lib.mjs`, and the HTTP helpers and the runnable story (`scripts/rp-client.mjs`, `scripts/rp-demo.mjs`) ship in the package too; a relying party written in another language reproduces them from the spec and checks
itself against the vectors.
