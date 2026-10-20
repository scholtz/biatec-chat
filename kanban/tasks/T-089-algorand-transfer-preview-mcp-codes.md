---
id: T-089
title: "Bug: Algorand transfer preview must use MCP network codes and the error field"
status: done
priority: P1
epic: "Wallet tools"
blocked_by: [T-082]
area: ["backend/Biatec.Chat.Tools/Wallet/Transfer/**", "backend/Biatec.Chat.Tests/Transfer/**"]
parallel: yes
conflicts_with: []
issue: "#168"
branch: "task/T-089"
owner: "worker-T-089"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-20
updated: 2026-10-20
---

# T-089 · Bug: Algorand transfer preview must use MCP network codes and the error field

## Context

Found by the T-082 worker while fixing wallet.balance against the real Biatec MCP schema (sibling BiatecMCP
`MCP/BiatecMCP.cs:84-345`, read only). `TransferPreviewService` (`Tools/Wallet/Transfer/TransferPreviewService.cs:58,75`)
calls `getCryptoBalance` with the raw chat network names `mainnet` / `testnet`, while MCP's codes are
`algorand-mainnet` / `algorand-testnet` (the rest of the backend already uses those), and it shapes the result on
its own, ignoring the `error` field that MCP uses for most failures. Against the real MCP, the balance check in the
wallet.transfer preview (opt-in detection, insufficient-funds message) can therefore fail or read garbage.

## Goal

The Algorand wallet.transfer preview reads balances exactly the way wallet.balance now does.

## Scope

- In: map chat network names to MCP codes; treat a non-empty `error` as a failed read with a clear message; read native
  ALGO from `nativeBalanceBaseUnits` and ASAs from `assets`, reusing the T-082 shaping (`McpToolResult.Failed`) instead of
  a second copy; tests against fakes that return the real MCP shapes.
- Out: Bitcoin transfers; signing.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] getCryptoBalance is called with `algorand-mainnet` / `algorand-testnet` (test)
- [x] An MCP `error` payload yields "the balance could not be read", never a shaped balance (test)
- [x] Native ALGO and ASA balances in the preview match the real MCP response shape (test)

## Verification

`dotnet test --filter Transfer`, then the full backend suite.

## Hand-off

- **Summary:** wallet.transfer preview and builder calls use the MCP network codes (`algorand-mainnet` / `algorand-testnet`) through one helper, `McpNetworks.Algorand` (Tools/Mcp), now also used by liquidity, safe.create, wallet.read and the transfer executor. `McpToolResult.Failed` fails on any non-empty `error` (string, object, array, number). Balances read the real MCP shape (native ALGO from `nativeBalanceBaseUnits`, ASAs from `assets`); a malformed native balance is a failed read. `Verify` checks the genesis hash as well as the genesis id. The DEX liquidity balance read had the same network/error bug and is fixed.
- **Branch:** `task/T-089` @ `67e2000` (merged with main), PR #169 (`Closes #168`).
- **Files outside the area:** `Tools/Mcp/McpTypes.cs`, `Api/Dex/Liquidity/LiquidityExecutors.cs`, `Api/Wallet/Transfer/WalletTransferExecutor.cs`, `Api/Safe/Create/SafeCreateExecutors.cs`, `Api/Wallet/Read/WalletReadExecutors.cs`, `Tests/Dex/Liquidity/LiquidityTests.cs`.
- **Verification:** Transfer + Liquidity 109 passed; full backend 1060 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed.
- **Known limitations:** two parsers of the MCP balance payload (wallet.balance's model JSON vs the typed transfer reader); `"algorand-" + network` still hand-built in SafeProposalRequests, SafeCreateRequest, X402PayService, SwapExecutor and one algod-options key (follow-up T-091).
- **Follow-up tasks created:** T-091 (adopt `McpNetworks.Algorand` in the remaining sites). T-090 (ASA decimals lookup, issue #170) becomes ready once this is done.
- **Unblocks:** T-090.

## Review

- 2026-10-20 04:00 · approve · PR #169 merged into main @ f948c28 (recorded by kanban-sync)

## Activity log

- 2026-10-20 05:10 · coordinator · (new) → blocked · proposed by T-082
- 2026-10-20 03:33 · kanban-board · backlog → blocked · waiting on: T-082
- 2026-10-20 03:33 · kanban-board · blocked → backlog · blockers done: T-082
- 2026-10-20 03:34 · LukassSoukup · taken · status stays backlog
- 2026-10-20 03:34 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 03:59 · coordinator · in-progress → review · branch task/T-089 @ 67e2000, PR #169
- 2026-10-20 04:00 · user · review → done · PR #169 merged into main @ f948c28 (synced by kanban-sync)
