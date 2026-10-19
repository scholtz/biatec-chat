---
id: T-030
title: dex.addLiquidity and dex.removeLiquidity capabilities
status: blocked
priority: P1
epic: "DEX"
blocked_by: [T-022, T-028]
area: ["backend/Biatec.Chat.Tools/Dex/Liquidity/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-19
updated: 2026-10-19
---

# T-030 · dex.addLiquidity and dex.removeLiquidity capabilities

## Context

If the user did not name a pool the assistant asks, showing each pool's fee, range and liquidity; preview carries pool app id, fee tier, range, verification class; broadcast via algod.

## Goal

Add liquidity to the EURS/USDC pool from chat.

## Scope

- In: Executors with clarifying question flow, preview, sign and submit via T-022.
- Out: Safe-proposed liquidity (T-033).

## Acceptance criteria

- [ ] Clarifying question lists pools
- [ ] Add liquidity lands on testnet and matches preview
- [ ] Remove liquidity works
- [ ] Tests with fakes

## Verification

dotnet test --filter Liquidity; testnet manual run

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
