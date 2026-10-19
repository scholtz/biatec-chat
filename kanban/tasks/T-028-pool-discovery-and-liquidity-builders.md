---
id: T-028
title: Pool discovery and liquidity builders
status: review
priority: P1
epic: "DEX"
blocked_by: [T-027]
area: ["builders/src/clamm/**"]
parallel: yes
conflicts_with: []
issue: "#29"
branch: "task/T-028"
owner: "session-20261019-2030"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-19
updated: 2026-10-19
---

# T-028 · Pool discovery and liquidity builders

## Context

Resolve configAppId, identityAppId, poolProviderAppId per network; list pools for a pair with fee tier, price range, liquidity; filter by verification class of the acting account; build add/remove liquidity and pool swap groups (clammAddLiquiditySender, clammRemoveLiquiditySender, clammSwapSender).

## Goal

All CLAMM building behind the sidecar.

## Scope

- In: Endpoints: pools/list, liquidity/add, liquidity/remove, swap/pool, each returning unsigned group + decoded facts.
- Out: Backend capabilities.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] Pool list for EURS/USDC on testnet returns fee and range (testnet has no EURS/USDC pool: verified on ALGO/USDC)
- [x] Add/remove liquidity groups simulate successfully
- [x] Ineligible verification class pools are excluded
- [x] Unit tests with fixtures

## Verification

pnpm --filter builders test; testnet simulate script

## Hand-off

- **Branch:** `task/T-028` @ PR #87. Code is in `builders/sidecar/src/clamm/` (the task's `area` says
  `builders/src/clamm/**`; the sidecar lives under `builders/sidecar/`).
- **Endpoints:** `POST /v1/query/clamm.pools.list` `{assetA, assetB, account?}` → pools with fee tier, price range,
  current price, liquidity, and `excluded` pools above the account's verification class. `POST /v1/build/`
  `clamm.liquidity.add` `{poolAppId, sender, amountA, amountB}`, `clamm.liquidity.remove` `{poolAppId, sender,
  lpAmount}`, `clamm.swap.pool` `{poolAppId, sender, fromAsset, fromAmount, minimumToReceive}` → unsigned group plus
  `facts` (pool, amounts, LP opt-in, tx ids, simulation, firstValid/lastValid). A group that fails simulation is
  refused (422). Only pools registered at the pool provider are built against; the algod's genesis id must match the
  network; locked identities and senders below the pool's class are refused.
- **Verification:** `pnpm --filter builders test` 22 passed; `pnpm --filter builders simulate:testnet`: 11 ALGO/USDC
  pools listed, add liquidity (5 txs), pool swap (2 txs) and remove liquidity (2 txs, a real LP holder) simulate OK.
- **Files outside the area:** `builders/sidecar/src/app.ts` (optional `facts` on build results, generic
  `/v1/query/:name` route, bigints as strings), `src/main.ts` (registers the CLAMM builders and queries),
  `package.json` / `pnpm-lock.yaml` (biatec-concentrated-liquidity-amm 0.9.44, algosdk 3.9.0, algokit-utils 9.2.2,
  pinned exactly: `clamm/lib.ts` loads all three as CommonJS because of the dual-package instanceof hazard).
- **For the capability tasks (T-029/T-030):** the backend's builders client must read `facts` (pool fee/range,
  amounts, validity window) for the preview; add/remove liquidity groups have no minimum-output bound because the
  library's `clammAddLiquidityTxs`/`clammRemoveLiquidityTxs` take none.
- **Known limits:** an account with under 0.01 ALGO above its minimum balance is treated as class 0 (the identity
  lookup is a simulated call the account pays for); every request re-reads the pool provider's box list (no cache);
  the CLAMM package pulls heavy unused dependencies (docusaurus, sharp, ipfs) into the image.
- **Open questions:** none.

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:10 · kanban-board · blocked → backlog · blockers done: T-027
- 2026-10-19 20:30 · LukassSoukup · taken · status stays backlog
- 2026-10-19 20:30 · session-20261019-2030 · backlog → in-progress · claimed
- 2026-10-19 20:49 · session-20261019-2030 · in-progress → review · PR #87
