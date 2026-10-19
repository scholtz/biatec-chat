---
id: T-029
title: dex.swap capability
status: backlog
priority: P1
epic: "DEX"
blocked_by: [T-022, T-028]
area: ["backend/Biatec.Chat.Tools/Dex/Swap/**"]
parallel: yes
conflicts_with: []
issue: "#30"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-029 · dex.swap capability

## Context

createSwapTransaction when Biatec Router wins; otherwise the user chooses a named Biatec DEX pool (clammSwapSender) or cancels; other routers out of scope.

## Goal

Swaps from chat with preview including slippage.

## Scope

- In: Executor calling MCP quote/build, fallback to sidecar pool swap with clarifying question, preview fields.
- Out: Non-Biatec routers.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Router path builds and previews
- [ ] Fallback asks the user to pick a pool
- [ ] Slippage and minimum received shown
- [ ] Tests with fakes

## Verification

dotnet test --filter Swap

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-028
