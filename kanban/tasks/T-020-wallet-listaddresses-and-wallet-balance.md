---
id: T-020
title: wallet.listAddresses and wallet.balance
status: backlog
priority: P0
epic: "Wallet tools"
blocked_by: [T-014, T-019]
area: ["backend/Biatec.Chat.Tools/Wallet/Read/**"]
parallel: yes
conflicts_with: []
issue: "#21"
branch: ""
owner: ""
taken_by: ""
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

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-014, T-019
