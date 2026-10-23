# Transferring funds

Signed-in users can ask Biatec AI to transfer funds from their own self-custody addresses, to another of their own addresses or to anyone else.

## What can be transferred

- ALGO, the native coin of Algorand.
- Algorand Standard Assets (ASAs), such as the stablecoins USDC and EURS.
- Bitcoin is not available yet: you can see your Bitcoin addresses and balances, but sending is switched off until Biatec Chat can sign Bitcoin transactions.

Examples: "Transfer 10 ALGO to my secondary address", "Send 25 USDC to ABC…XYZ".

## How a transfer works

1. The assistant builds the transaction and shows a preview with the source, the full destination address, the asset, amount, fee and remaining limit.
2. You review and confirm. The first time in a session, you grant signing permission in a popup.
3. Above your approval threshold, you approve on your phone in Biatec Authenticator.
4. Biatec OIDC signs the transaction within your spending limits, and it is submitted to the network.
5. The card shows the transaction ID and an explorer link.

## Sending an asset to an address that hasn't opted in

On Algorand an address must opt in to an asset before it can receive it. When you send an asset to another of your own addresses that hasn't opted in yet, the assistant adds the steps for you: if needed, a small ALGO payment to cover that address's minimum balance and fee, then the opt-in from that address, then the transfer. Every step is numbered on the card and confirmed one after another. For someone else's address, the receiver has to opt in themselves.

## Bitcoin transfers (not available yet)

Bitcoin transfers are switched off until Biatec Chat can sign them; the assistant says so if you ask. When they are available, a Bitcoin transfer spends from a single address. The preview shows the decoded inputs, outputs, change and fee rate. A Bitcoin preview expires after 30 minutes, or sooner if its inputs are spent or the fee rate moves by more than 25 percent; the assistant then builds a fresh one.

## Always check the destination

Blockchain transfers can't be reversed. The preview always shows the full destination address; compare it carefully, especially when you pasted it. The assistant never moves funds from an address you don't control.
