---
id: T-028
title: Pool discovery and liquidity builders
status: backlog
priority: P1
epic: "DEX"
blocked_by: [T-027]
area: ["builders/src/clamm/**"]
parallel: yes
conflicts_with: []
issue: "#29"
branch: ""
owner: ""
taken_by: ""
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

- [ ] Pool list for EURS/USDC on testnet returns fee and range
- [ ] Add/remove liquidity groups simulate successfully
- [ ] Ineligible verification class pools are excluded
- [ ] Unit tests with fixtures

## Verification

pnpm --filter builders test; testnet simulate script

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:10 · kanban-board · blocked → backlog · blockers done: T-027
