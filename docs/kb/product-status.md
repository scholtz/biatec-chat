# Biatec Chat: what's available now and what's coming

Biatec Chat was built at a fintech hackathon in October 2026. This page says honestly what works today and what depends on changes still to come in other Biatec products.

## Available

- Guest chat on Biatec sites and any website: explanations, knowledge base answers, 24-hour market snapshots, sign-in card.
- Sign-in with Google or Microsoft through Biatec OIDC in a popup, with no tokens in the browser.
- Addresses and balances, transfers, swaps, adding and removing liquidity, and Biatec Safe create, propose, approve and execute, each with a preview bound to the exact transaction.
- One-time signing permission per session, signing at Biatec OIDC within the user's spending limits.
- Agent modes, pause and revoke, Activity timeline.
- Hash-chained, signed evidence record with export and verification keys; on-chain anchoring.
- English and Slovak; the `@biatec/chat-widget` npm package for other sites.

## Built, waiting on Biatec OIDC

- **Phone approval in Biatec Authenticator.** The consent flow, number matching, receipts and the app are built and tested against a test version of Biatec OIDC. The live Biatec OIDC doesn't have the consent endpoints yet, so actions that need phone approval can't complete against it yet, and the app hasn't been released to stores.
- **Biatec Consent Protocol for partners.** Specification, schemas, test vectors, mock and conformance suite are published; live endpoints and dynamic client registration will follow.
- **Changing spending limits from the chat** needs the site to be allowlisted for limit management.
- **Immediate token revocation at logout** waits on a revocation endpoint at Biatec OIDC; until then tokens are deleted from the backend and age out.

## Built, switched off

- **x402 payments**: behind a feature flag, tested only against a test facilitator.

## Not yet planned for version 1

Rule-based automation without per-action confirmation, asset creation, bridges, rekeying, conversation history across sessions, and file attachments.

## Networks

Demonstrations run on Algorand testnet, where tokens have no value. Every action is labelled with its network.
