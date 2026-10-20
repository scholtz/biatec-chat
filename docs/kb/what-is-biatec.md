# What is Biatec

Biatec is a family of self-custody finance products built on the Algorand blockchain. Self-custody means the user, not Biatec, controls the keys to their funds: Biatec never holds customer money and cannot move it on its own.

## The Biatec products in one paragraph

Biatec OIDC lets people sign in with Google or Microsoft and get a self-custody wallet. Biatec MCP gives AI assistants safe, step-by-step access to that wallet. Biatec DEX is a concentrated-liquidity exchange on Algorand. Biatec Safe (Algo Safe) is a shared smart account where a group approves payments together. Biatec Web is the product site. Biatec Chat, shown to users as Biatec AI, is the assistant that ties them together on every Biatec page. The page "Biatec products" describes each one.

## What problem Biatec Chat solves

Biatec already has the hard parts of self-custody finance: a wallet you sign into with an account you already have, an exchange, shared accounts and AI tooling. What was missing is the front door. Without the assistant, a user has to know which product does what, where the buttons are and how a blockchain transaction is put together. Biatec Chat turns that into a conversation: you ask in plain language, and the assistant explains, or prepares the action and shows you exactly what will happen before anything is signed.

## How Biatec keeps users safe

- Keys stay with the user. Biatec OIDC derives Algorand, EVM and Bitcoin keys and stores them encrypted in the user's own cloud storage.
- The assistant never holds keys, and the browser never receives a login token.
- Every action shows a preview first, and nothing moves until the user confirms it.
- Spending limits the user sets (daily, weekly, monthly) are enforced by Biatec OIDC, outside the chat.
- Larger actions, above 1000 EUR by default, need approval on the user's phone in the Biatec Authenticator app.
- Every conversation leaves a tamper-evident record the user can download.

## Who Biatec Chat is for

- Visitors who want to understand Biatec, Algorand or decentralised finance, without creating an account.
- Signed-in Biatec users who want to check balances, transfer funds, swap assets, provide liquidity or run a shared Biatec Safe by asking in plain language.
- Teams and small organisations that need several people to approve a payment.
- Banks, fintechs and websites that want to embed the assistant or ask Biatec users to approve actions on their phone (see "For banks, fintechs and partners" and "Embedding Biatec Chat on your website").

## The vision

Biatec Chat should feel like talking to a knowledgeable, careful banker who is available on every Biatec page: accurate answers for anyone, clear previews for signed-in users, phone approval for large amounts, and a trail of evidence for everything. Longer term the same backend and phone approval serve automations, agent-to-agent payments and third-party banks.
