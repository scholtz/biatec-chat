# How Biatec Chat keeps your funds safe

Biatec Chat is designed so that the AI can propose but never act outside what you authorised.

## Keys never leave Biatec OIDC

Your wallet keys are held by Biatec OIDC, encrypted in your own cloud storage. Biatec Chat, the AI model and the website never see them.

## No tokens in the browser

The browser receives only an opaque session cookie that scripts can't read. Login tokens are kept by the backend, encrypted, and deleted when your session ends. A malicious script on a page has nothing to steal.

## The AI only proposes

The AI model suggests actions; deterministic code builds them, shows the preview, and asks you to confirm. Guests' requests to the AI don't even include wallet tools, and every tool call is checked on the server against your tier, permissions and agent mode, whatever the AI says.

## What you see is what you sign

Each preview is bound to a fingerprint (group hash) of the exact transaction. The same bytes are approved, signed and submitted. A transaction that differs by one byte is refused by Biatec OIDC.

## Limits and approvals outside the chat

Spending limits, the phone approval threshold, cooling-off for new apps and first-use approval are enforced by Biatec OIDC, not by the chat. Even if the chat backend were compromised, above-threshold actions would still need your phone.

## Phone approval that can't be faked

Biatec Authenticator renders the approval from the transaction itself, fetched from Biatec OIDC. You type a code shown only in the chat (number matching), so a request you didn't start can't be approved by reflex. The device key lives in the phone's secure hardware.

## Protection against prompt injection

Content from outside the user, such as knowledge base pages, token names, on-chain notes or page information, is passed to the AI as untrusted data. It can never change your tier, your permissions or the action. A Bedrock guardrail also screens messages and tool results.

## Impostor protection

Only apps verified through OpenID Federation show a name and logo on your phone; others appear as "Unverified application". An impostor can't present itself as your bank.

## Tamper-evident record

Every conversation leaves a signed, hash-chained evidence record, anchored on Algorand every 10 minutes.

## What remains possible

The honest residual risk is spending below your phone approval threshold, within your spending limits and the hourly safety cap, by a site you are signed into. You can shrink it by lowering limits or turning on "always ask".

## Staying safe

- Never share your seed phrase, password or recovery code. Biatec AI will never ask for them.
- Compare the amount, asset and destination on your phone with the chat before approving.
- If you didn't start a request, deny it.
- Check the network label (Testnet or Mainnet) and asset IDs.
