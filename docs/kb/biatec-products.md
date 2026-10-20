# Biatec products

Biatec is a set of products that work together. All of them are self-custody: the user controls the keys.

## Biatec OIDC (sign-in and wallet)

Biatec OIDC, at oidc.biatec.io, is a sign-in service (an OpenID Connect provider) and a self-custody wallet. Users sign in with an existing Google or Microsoft account. Biatec OIDC derives the user's Algorand, EVM (Ethereum-compatible) and Bitcoin keys and stores them encrypted in the user's own cloud storage, not on a Biatec server. Apps the user signs into, such as Biatec Chat, can ask Biatec OIDC to sign a transaction only within the daily, weekly and monthly spending limits the user controls. Biatec OIDC also decides when an action needs phone approval in the Biatec Authenticator app.

## Biatec MCP (wallet tools for AI assistants)

Biatec MCP, at mcp.biatec.io, exposes the Biatec wallet to AI assistants over the Model Context Protocol, a standard way for AI models to use tools. It builds payments, asset opt-ins, swaps and other transactions, and broadcasts signed transactions to the network. Building, signing and submitting are separate steps, so an unsigned transaction can be inspected before it is ever sent. Biatec Chat uses Biatec MCP to read addresses and balances and to build and broadcast transactions; signing always goes directly to Biatec OIDC.

## Biatec DEX (exchange)

Biatec DEX is a decentralised exchange on Algorand built on BiatecCLAMM, a concentrated-liquidity automated market maker (AMM). Users swap assets straight from their own wallet, and liquidity providers place liquidity in a price range of their choice. Biatec Router compares prices with other aggregators. See "Biatec DEX concepts", "Swapping assets" and "Providing liquidity".

## Biatec Safe (Algo Safe)

Biatec Safe, built on the open-source Algo Safe, is a shared smart account on Algorand. A group of signers controls it with an M-of-N rule, such as 2-of-3: a payment runs only after enough members approve it. It supports spending limits, several signer groups and agent payments over x402. See "Biatec Safe".

## Biatec Web

Biatec Web, at web.biatec.io, is the Biatec product and marketing site. Biatec Chat appears on it in the bottom-right corner.

## Biatec Chat (Biatec AI)

Biatec Chat is the AI assistant shown to users as Biatec AI. It lives in the corner of Biatec Web and Biatec DEX and can be embedded in other websites. Anyone can ask it questions; signed-in users can prepare transfers, swaps, liquidity changes and safe actions, each with a preview. See "Biatec Chat assistant".

## Biatec Authenticator

Biatec Authenticator is a mobile app for iOS and Android, similar in role to Microsoft Authenticator. It shows the details of a pending high-value action and lets the user approve or deny it with a key bound to the phone. It holds no wallet keys. See "Phone approval with Biatec Authenticator".

## Biatec Consent Protocol

The Biatec Consent Protocol is an open specification that lets any bank, fintech or app ask a Biatec user to approve one precise action on their Biatec Authenticator, without asking Biatec for permission, while making it impossible for an impostor to pose as that bank. See "For banks, fintechs and partners".
