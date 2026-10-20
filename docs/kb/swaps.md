# Swapping assets

Signed-in users can ask Biatec AI to swap one asset for another, for example "Swap 50 USDC to ALGO" or "Swap 100 USDC to EURS".

## How the price is found

The assistant asks Biatec MCP for a quote. Biatec MCP compares Biatec Router with other aggregators on Algorand and builds the swap through Biatec Router when it offers the best result. Otherwise the swap can go through a Biatec DEX pool you choose.

## What the swap preview shows

- What you give and the minimum you will receive, for example "Swap 100 USDC for at least 91.80 EURS on Biatec DEX".
- The pool or route, the fee tier and network fees.
- The slippage setting: how much worse than the quoted price you accept.
- A simulation of the transaction and the spending limit it counts against.

If the price moves beyond your slippage before the swap runs, the transaction fails and nothing is swapped; you only pay the small network fee.

## Before you swap

- Your address must have opted in to the asset you receive. The assistant tells you if an opt-in is needed.
- Keep enough ALGO for fees and your minimum balance.
- Prices can move. The assistant states the facts of the quote but doesn't advise whether or when to swap.

## Is a swap safe?

The swap runs from your own wallet; no one takes custody of your funds. You see the exact transaction before signing, signing stays within your spending limits, and large swaps need phone approval.
