---
id: T-089
title: "Bug: Algorand transfer preview must use MCP network codes and the error field"
status: blocked
priority: P1
epic: "Wallet tools"
blocked_by: [T-082]
area: ["backend/Biatec.Chat.Tools/Wallet/Transfer/**", "backend/Biatec.Chat.Tests/Transfer/**"]
parallel: yes
conflicts_with: []
issue: "#168"
branch: ""
owner: ""
taken_by: ""
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

- [ ] getCryptoBalance is called with `algorand-mainnet` / `algorand-testnet` (test)
- [ ] An MCP `error` payload yields "the balance could not be read", never a shaped balance (test)
- [ ] Native ALGO and ASA balances in the preview match the real MCP response shape (test)

## Verification

`dotnet test --filter Transfer`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 05:10 · coordinator · (new) → blocked · proposed by T-082
- 2026-10-20 03:33 · kanban-board · backlog → blocked · waiting on: T-082
