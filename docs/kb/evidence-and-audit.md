# Evidence and audit trail

Every Biatec Chat conversation produces an append-only evidence record: what you asked, what the assistant proposed, what you approved, and which transaction landed on the blockchain.

## What is recorded

- Session events: sign-in, mode changes, the site and widget version, timestamps.
- Every message, including the tools the assistant used and their results (secrets removed).
- Every action preview, each step with its transaction fingerprint, and your confirmation.
- Every phone approval request and the receipt signed by your device.
- Every signing request to Biatec OIDC and its outcome, including spending-limit decisions.
- Every submitted transaction ID, confirmation round and final status.

## Why it can't be edited quietly

Each entry is hashed and chained to the previous one, and the backend signs each entry with a key published for verification. Changing or removing an entry breaks the chain. Every 10 minutes the backend also anchors the latest state of open conversations on the Algorand blockchain, so even the operator can't rewrite history without detection. Approval receipts are signed by your phone, and transactions are on the blockchain, so neither can be forged by the backend.

## Downloading your evidence

After an action, select Download evidence on the receipt card, or export sessions from the widget. The bundle is signed JSON. An auditor, a bank or you can verify the hash chain, the signatures, the approval receipts and the transaction IDs offline, without trusting Biatec.

## What evidence is used for

Dispute handling, audits, compliance reviews and proving exactly what an AI assistant did on a user's behalf and with what consent.

## Privacy and deletion

Message contents can be deleted on request while the chain still verifies, because each entry stores only a salted fingerprint of its content. See "Privacy and your data".
