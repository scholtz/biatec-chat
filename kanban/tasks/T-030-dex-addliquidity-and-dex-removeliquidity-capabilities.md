---
id: T-030
title: dex.addLiquidity and dex.removeLiquidity capabilities
status: in-progress
priority: P1
epic: "DEX"
blocked_by: [T-022, T-028]
area: ["backend/Biatec.Chat.Tools/Dex/Liquidity/**"]
parallel: yes
conflicts_with: []
issue: "#31"
branch: "task/T-030"
owner: "session-20261019-agent"
taken_by: "scholtz"
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

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

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
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-028
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
