# Biatec DEX concepts

## What is a DEX?

A DEX (decentralised exchange) lets you swap assets directly from your own wallet. Nobody takes custody of your funds; a smart contract on the blockchain holds the pool and executes the trade.

## What is an AMM?

An automated market maker (AMM) prices trades from the assets held in a pool instead of an order book. Biatec DEX is built on BiatecCLAMM, a concentrated-liquidity AMM on Algorand.

## What is concentrated liquidity?

In a classic pool, liquidity is spread over every possible price. In a concentrated-liquidity pool, each liquidity provider chooses a price range. Liquidity is used only while the price is inside that range, so the same capital supports more trading and earns fees more efficiently, but it stops earning when the price moves outside the range.

## Fee tier

Every pool has a fee tier set when the pool is created. Each swap pays that percentage to the pool's liquidity providers. Several pools can exist for the same pair with different fee tiers and ranges. The fee tier is shown on the pool and in every swap preview.

## Slippage

Slippage is the difference between the quoted price and the price you actually get, caused by the trade's size and other trades in the meantime. Your slippage setting caps how much worse a price you accept; if it would be exceeded, the swap fails instead.

## Price impact

A large trade relative to the pool's liquidity moves the price more. The preview shows the minimum you will receive, which already includes it.

## LP tokens

When you add liquidity you receive LP tokens representing your share of the pool. Returning them withdraws your assets plus your share of fees.

## Biatec Router

Biatec Router finds a route for a swap. Biatec Chat compares it with other aggregators and uses it when it gives the best result.

## Market snapshot

Anyone can ask Biatec AI for a 24-hour market snapshot of a pair from public Biatec DEX data. See "Market data".

## Who can do what

Anyone can ask the assistant to explain the DEX. Swaps and liquidity changes need a signed-in account.
