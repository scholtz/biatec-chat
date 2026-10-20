---
id: T-091
title: Adopt McpNetworks.Algorand in the remaining hand-built network codes
status: blocked
priority: P3
epic: "Wallet tools"
blocked_by: [T-089]
area: ["backend/Biatec.Chat.Tools/Safe/Proposals/SafeProposalRequests.cs", "backend/Biatec.Chat.Tools/Safe/Create/SafeCreateRequest.cs", "backend/Biatec.Chat.Tools/X402/**", "backend/Biatec.Chat.Tools/Dex/Swap/**"]
parallel: yes
conflicts_with: []
issue: "#174"
branch: ""
owner: ""
taken_by: ""
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

- [ ] `grep '"algorand-" +'` finds no MCP network code outside the helper
- [ ] Full backend suite green

## Verification

`grep -rn '"algorand-" +' backend --include=*.cs`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 06:30 · coordinator · (new) → blocked · proposed by T-089
