# Providing liquidity on Biatec DEX

Liquidity providers deposit a pair of assets into a Biatec DEX pool so that others can trade against it. In return, the pool pays them a share of trading fees. Signed-in users can ask Biatec AI to add or remove liquidity, for example "Add liquidity to the Biatec DEX EURS/USDC pool".

## Adding liquidity

1. Tell the assistant the pair and amounts. If several pools exist for the pair, it asks which one, showing each pool's fee tier and price range.
2. The preview shows the pool application ID, fee tier, price range, the amounts of both assets, fees and the spending limit it counts against.
3. You confirm, and approve on your phone if the value is above your threshold.
4. You receive LP tokens (liquidity provider tokens) that represent your share of the pool. Your address opts in to the LP token if needed.

## Removing liquidity

Ask "Remove my liquidity from the EURS/USDC pool". The preview shows what you will get back: both assets, including your share of accumulated fees, in proportions that depend on the current price.

## Risks to understand

- Price movement changes the mix of assets you hold in the pool. When the price leaves your range, your position holds only one of the two assets and stops collecting fees. If the price moved a lot, the value you withdraw can be lower than simply holding the two assets (often called impermanent loss).
- Fees are not guaranteed; they depend on trading volume.
- The assistant explains how pools work but doesn't recommend pools, ranges or amounts.

## Pool eligibility

Some Biatec DEX pools are restricted to accounts with a certain identity verification class. The assistant tells you when a pool isn't open to your account.

## Liquidity from a Biatec Safe

A Biatec Safe can also provide liquidity. A member proposes the add-liquidity transaction group, other members approve it, and the safe executes it. See "Biatec Safe".
