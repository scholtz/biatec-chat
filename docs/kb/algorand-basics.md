# Algorand and blockchain basics

## What is a blockchain?

A blockchain is a shared ledger kept by many independent computers. Transactions are grouped into blocks, and each block is cryptographically linked to the previous one, so history can't be changed quietly. Whoever holds an account's private key controls that account; there is no central operator who can move or freeze funds on a whim.

## Why Biatec uses Algorand

Algorand is a proof-of-stake blockchain with fast finality, a new block about every three seconds with no forks, so a confirmed transaction is final. Fees are tiny (0.001 ALGO for a basic transaction), and it has built-in features Biatec relies on: native tokens (Algorand Standard Assets), atomic transaction groups, smart contracts and transaction simulation before signing.

## ALGO and Algorand Standard Assets

ALGO is Algorand's native coin, used for fees and minimum balances. Other tokens are Algorand Standard Assets (ASAs), each with a numeric asset ID. Common ones in Biatec are the stablecoins USDC (asset ID 31566704 on mainnet, 10458941 on testnet) and EURS, plus LP tokens from Biatec DEX pools. Always check the asset ID, because anyone can create an asset with a familiar name.

## Opt-in

An Algorand account must opt in to an asset before it can receive it. An opt-in is a zero-amount transfer of that asset to yourself. Biatec AI adds the opt-in step when one of your addresses needs it.

## Minimum balance

Every Algorand account must keep a minimum ALGO balance: 0.1 ALGO for the account, plus 0.1 ALGO for each asset it has opted in to and more for each application it uses. This ALGO isn't spent, but it can't be sent while the account holds those assets. Fund a new address with some ALGO first.

## Transaction groups

Algorand can bundle up to 16 transactions into an atomic group: all succeed or all fail. A swap or a liquidity deposit is a group. The group ID is the fingerprint Biatec uses to bind a preview to exactly what gets signed.

## Validity window

A signed Algorand transaction is valid for at most 1000 rounds, about 45 minutes. That's why previews show a countdown and expire.

## Testnet and mainnet

Testnet is a practice network whose tokens have no value; mainnet is the real network. Biatec AI labels the network at every action, and nothing simulated is presented as real.

## Explorers

A blockchain explorer is a website that shows transactions and accounts. After an action, Biatec AI links the transaction ID to an explorer so you can verify it independently.

## Other chains

Biatec OIDC also derives EVM (Ethereum-compatible) and Bitcoin addresses. In Biatec Chat, Bitcoin transfers are supported; bridging between chains is not.
