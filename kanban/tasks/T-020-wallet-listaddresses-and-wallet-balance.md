---
id: T-020
title: wallet.listAddresses and wallet.balance
status: done
priority: P0
epic: "Wallet tools"
blocked_by: [T-014, T-019]
area: ["backend/Biatec.Chat.Tools/Wallet/Read/**"]
parallel: yes
conflicts_with: []
issue: "#21"
branch: "task/T-020"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-020 · wallet.listAddresses and wallet.balance

## Context

Authenticated users can read their own addresses and balances via MCP.

## Goal

First authenticated capabilities end to end.

## Scope

- In: Executors mapping the capability tools to MCP wrappers, result shaping for the model, tool status lines.
- Out: Transfers.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Signed-in chat lists addresses
- [ ] Balance query returns amounts with decimals
- [ ] Anonymous session cannot call them
- [ ] Tests with fake MCP

## Verification

dotnet test --filter WalletRead

## Hand-off

## Review

- 2026-10-19 22:28 · approve · PR #98 merged into main @ 0cf6b13 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-014, T-019
- 2026-10-19 21:24 · scholtz · taken · status stays backlog
- 2026-10-19 21:24 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 22:28 · user · in-progress → done · PR #98 merged into main @ 0cf6b13 (synced by kanban-sync)
