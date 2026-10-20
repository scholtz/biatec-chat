# For banks, fintechs and partners

Biatec offers two ways to build on its products: embed the Biatec Chat assistant under your own domain, or use the Biatec Consent Protocol to have Biatec users approve your requests on their Biatec Authenticator.

## White-label Biatec Chat for your site

A bank or fintech can run Biatec Chat as its own tenant. You point a subdomain such as chat.example-bank.com at the Biatec Chat backend, prove ownership with a DNS record, and register your site's origins. TLS certificates are issued and renewed automatically, or you can upload your own. Each tenant has its own settings, such as the AI model, sign-in page, the safety cap and which features are on. Your users then get the full signed-in experience on your site. See "Embedding Biatec Chat on your website".

## The Biatec Consent Protocol

The Biatec Consent Protocol lets any service ask a Biatec user to approve one precise action on their phone, without asking Biatec for permission, while impostors can't pretend to be you.

- **Built on standards banks already know**: OpenID Connect CIBA (backchannel authentication), FAPI 2.0, Rich Authorization Requests (RFC 9396), OpenID Federation for verified identity, and Kantara-style consent receipts. It works like PSD2 strong customer authentication with dynamic linking: the approval is tied to the exact transaction.
- **Verified identity**: publish an OpenID Federation entity configuration on your domain and chain it to a recognised trust anchor, and the phone shows your name, logo and domain. Unverified clients appear as "Unverified application".
- **Two ways in**: the step-up entry (call the wallet signing endpoint and receive a challenge when approval is needed) or the direct entry (send the unsigned transaction first and get the approval in hand). Both produce the same signed receipt.
- **What the user approves is the transaction, not your description.** Biatec OIDC decodes the transaction group itself; client-supplied amounts or payees are never what is approved.
- **Receipts verifiable offline**: signed by the user's device key, with an attestation from Biatec OIDC, checkable with the published keys.

## What's available to partners today

The protocol specification, JSON schemas, signed test vectors, a mock Biatec OIDC and a conformance test suite are published in the Biatec Chat repository (`packages/consent-protocol`). The consent endpoints in the live Biatec OIDC are not available yet, and client registration is reviewed by an operator. Partners can build and test against the mock now and switch the address when the live endpoints ship.

## Why a shared authenticator

Users get one trusted device for approvals across banks and apps, with consistent number matching and verified names, which makes impersonation hard. Partners get a ready-made approval surface and a standard consent record.

## Rails

Today Biatec Chat works with Algorand assets, including stablecoins such as USDC and EURS, and Bitcoin. The consent protocol itself doesn't depend on the payment rail.

## Contact

Partners interested in a tenant or the consent protocol should contact Biatec through web.biatec.io.
