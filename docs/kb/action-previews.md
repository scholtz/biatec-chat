# Action previews: see exactly what will happen

Every action that would move funds or change your limits produces an action preview card before anything is signed. The card is built by the backend from the actual transaction, not written by the AI model.

## What the preview shows

- What will happen in plain words, for example "Swap 100 USDC for at least 91.80 EURS on Biatec DEX".
- The network, labelled Testnet or Mainnet.
- The source address and the destination in full (or the DEX pool with its fee tier and price range).
- Assets, amounts, and the value in EUR used for safety decisions, when it is known.
- Network fees, and slippage or the worst-case outcome.
- A simulation of the transaction on Algorand where available (for Bitcoin, the decoded inputs, outputs, change and fee rate).
- Which spending limit it counts against and how much remains.
- Whether phone approval in Biatec Authenticator is expected, and why.

## Review, then Confirm

Confirm is enabled only after you open Review. Review answers what will happen, which permissions are involved, the exact details, the financial impact (including, when the EUR value is known, what a 10 percent fall in price would cost) and how to stop it. The Confirm button names the action and amount, for example "Confirm swap · 100 USDC". Cancel discards the preview. Until you confirm, the card has a dashed border; after, a solid one.

## Why previews expire

An Algorand transaction is valid for at most 1000 rounds, about 45 minutes, and a Bitcoin preview expires after 30 minutes. The card shows a countdown. Confirm is disabled once less than 10 minutes remain, so there is time for phone approval, signing and broadcasting. An expired preview cannot be confirmed: ask again and the backend builds a fresh one for you to check.

## What you confirm is what gets signed

Each preview is bound to a fingerprint of the exact transaction, called the group hash. The same bytes are previewed, approved on your phone, signed and submitted. If anything changes, even by one byte, the fingerprint no longer matches and Biatec OIDC refuses to sign. Nobody, including the AI or a compromised website, can swap in a different transaction after you confirmed.

## Multi-step actions

Some actions need several steps, for example funding an address with ALGO, then opting it in to an asset, then transferring that asset. The card numbers every step. Each step is built and confirmed after the previous one lands on the blockchain, so each has its own fresh validity window. Steps that already landed are never repeated; they are shown as completed with their transaction IDs.

## Status of a preview

Awaiting confirmation or approval, Approved and signing, Submitted, Completed, Rejected, Failed, Cancelled or Expired. Every status shows a colour, an icon and a word.

## Receipts

When an action completes, the card shows the transaction ID with a blockchain explorer link, and a Download evidence link for the session's signed record.
