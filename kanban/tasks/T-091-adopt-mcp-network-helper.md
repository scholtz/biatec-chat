---
id: T-091
title: Adopt McpNetworks.Algorand in the remaining hand-built network codes
status: done
priority: P3
epic: "Wallet tools"
blocked_by: [T-089]
area: ["backend/Biatec.Chat.Tools/Safe/Proposals/SafeProposalRequests.cs", "backend/Biatec.Chat.Tools/Safe/Create/SafeCreateRequest.cs", "backend/Biatec.Chat.Tools/X402/**", "backend/Biatec.Chat.Tools/Dex/Swap/**"]
parallel: yes
conflicts_with: []
issue: "#174"
branch: "task/T-091"
owner: "worker-T-091"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-20
updated: 2026-10-20
---

# T-091 · Adopt McpNetworks.Algorand in the remaining hand-built network codes

## Context

T-089 added `McpNetworks.Algorand` (Tools/Mcp/McpTypes.cs) after the chat sent raw `mainnet` / `testnet` to Biatec MCP,
which expects `algorand-mainnet` / `algorand-testnet`. These sites still build the code by hand:
`Tools/Safe/Proposals/SafeProposalRequests.cs:59/103`, `Tools/Safe/Create/SafeCreateRequest.cs:17`,
`Tools/X402/X402PayService.cs:136/190`, `Tools/Dex/Swap/SwapExecutor.cs:280` (and an algod options key in
`Api/Wallet/Transfer/WalletTransferExecutor.cs:245`, which is not an MCP code and stays).

## Goal

One definition of the MCP network codes, so the next naming change can't miss a call site.

## Scope

- In: replace each hand-built MCP code with the helper; no behaviour change (tests stay green).
- Out: algod option keys; Bitcoin.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] `grep '"algorand-" +'` finds no MCP network code outside the helper
- [x] Full backend suite green

## Verification

`grep -rn '"algorand-" +' backend --include=*.cs`, then the full backend suite.

## Hand-off

- **Summary:** the four listed sites build MCP network codes with `McpNetworks.Algorand`; no behaviour change. Left: algod option keys (WalletTransferExecutor.cs:245) and the prefix parser (AlgodTransactionConfirmer.cs:130).
- **Branch:** `task/T-091`, PR #189 (`Closes #174`).
- **Verification:** grep clean; full backend 1172 passed, 1 skipped, 0 failed (with `DOTNET_USE_POLLING_FILE_WATCHER=1` while parallel suites exhausted the machine's inotify limit).
- **Unblocks:** no task is blocked by T-091.

## Review

- 2026-10-20 05:25 · approve · PR #189 merged into main @ 6b85d23 (recorded by kanban-sync)

## Activity log

- 2026-10-20 06:30 · coordinator · (new) → blocked · proposed by T-089
- 2026-10-20 04:00 · kanban-board · blocked → backlog · blockers done: T-089
- 2026-10-20 05:08 · LukassSoukup · taken · status stays backlog
- 2026-10-20 05:08 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 05:24 · coordinator · in-progress → review · PR #189
- 2026-10-20 05:25 · user · review → done · PR #189 merged into main @ 6b85d23 (synced by kanban-sync)
