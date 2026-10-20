# Privacy and your data

## What Biatec Chat stores

- For signed-in users: your Biatec OIDC user identifier, your addresses, the conversation (your messages, the assistant's answers and the tools it used), action previews, approvals and transaction IDs.
- For guests: only what you choose to type in the conversation.
- No IP addresses or location are recorded in the conversation record.
- Secrets such as login tokens are never stored in the conversation; they are removed by design.

Login tokens are kept only by the Biatec Chat backend, encrypted, and deleted when your session ends.

## How long data is kept

The defaults, which each deployment can configure:

- Guest conversations: 30 days.
- Signed-in conversations: 5 years.
- For actions that moved funds, the evidence (the hash chain, approval receipts and transaction IDs) is kept 7 years, in line with financial record-keeping. What you were shown stays reproducible from the signed receipt.

## Exporting your data

Signed-in users can view and export their own sessions from the widget. The export is a signed evidence bundle in JSON that anyone can verify independently (see "Evidence and audit trail").

## Deleting your data

A signed-in user can ask for their conversations to be deleted. The message contents are removed immediately and the link to your identity is cleared. What remains are fingerprints (hashes), transaction IDs and approval receipts, which reveal nothing about the deleted content but keep the record verifiable. Transactions already on the blockchain are public and can't be deleted by anyone.

Guest conversations can't be tied to a person, so they can't be deleted on request; they are removed automatically after 30 days.

## Where data is processed

The AI runs on Anthropic Claude models through Amazon Bedrock in the EU (Frankfurt, eu-central-1). Conversation data is stored by the Biatec Chat backend.

## Data minimisation

Biatec Chat asks for nothing beyond what an action needs. It never asks for seed phrases, passwords or private keys. Pages that embed the widget share page information with it only when the site owner explicitly turns that on.
