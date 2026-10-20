---
id: T-082
title: "Bug: wallet.listAddresses and wallet.balance must cover Bitcoin"
status: done
priority: P1
epic: "Wallet tools"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Wallet/Read/**", "backend/Biatec.Chat.Tests/Wallet/**", "backend/Biatec.Chat.Core/Capabilities/CapabilityRegistry.cs"]
parallel: no
conflicts_with: [T-087, T-067]
issue: "#151"
branch: "task/T-082"
owner: "worker-T-082"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-20
updated: 2026-10-20
---

# T-082 · Bug: wallet.listAddresses and wallet.balance must cover Bitcoin

## Context

Spec capability table: `wallet.listAddresses`, `wallet.balance` are backed by *"Biatec MCP listAlgorandAddresses,
getCryptoAddress, getCryptoBalance"*. Sample prompt: *"Transfer funds from my primary Bitcoin address to my secondary
Bitcoin address."* Success criterion 3: *"A user can transfer between their own Bitcoin addresses"*.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #5).

- `CapabilityRegistry.cs:158-160`: `wallet.listAddresses` takes `networkOnly` (enum `mainnet|testnet`) and is
  described as "List the user's Algorand addresses"; `wallet.balance` uses the same `Network()` enum.
- `BiatecMcpClient.GetCryptoAddressAsync` exists (`Tools/Mcp/BiatecMcpClient.cs:84`) but no executor calls it.
- T-024 (done) added `bitcoin` and `bitcoin-testnet` only to `wallet.transfer`.
- Effect: the model cannot discover the user's Bitcoin addresses or balances, so the Bitcoin transfer between own
  addresses in success criterion 3 needs the user to paste addresses and amounts blind.
- Check what `getCryptoAddress` returns (one address per network? index parameter for primary/secondary?) from
  Biatec MCP's tool schema before designing the input.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-082 in biatec-chat. Read the capability table and success criterion 3 in PRODUCT-DESCRIPTION.md.
Read backend/Biatec.Chat.Api/Wallet/Read/**, the wallet.* entries in CapabilityRegistry.cs, Tools/Mcp/* and the
Bitcoin network names T-024 introduced (Tools/Wallet/Bitcoin/**). Find the real input/output schema of Biatec MCP
getCryptoAddress and getCryptoBalance (MCP tool list, sibling repo docs; do not modify sibling repos). Decide how
"primary" and "secondary" Bitcoin addresses are identified and how balances in sats are shaped for the model.
>
> Report: (a) does the conflict still exist on current `main`, with file:line evidence; (b) is the spec text
> unambiguous, quoting it; (c) the smallest change that satisfies it and what it touches outside this task's `area`;
> (d) risks and tests needed. End with a verdict line: `APPROVED`, `PARTIAL: <approved items>` or
> `REJECTED: <reason>`, and write it into the task's Analysis verdict section.

## Analysis gate

This task starts with an analysis step. **No code may be written until the Analysis verdict below says `APPROVED`
or `PARTIAL`.**

1. Run the **Analysis prompt** above against the current `main` (the finding was recorded on `main @ 6b701e5`;
   later merges may already have fixed or changed it).
2. Write the result into **Analysis verdict**: `APPROVED` (implement as scoped), `PARTIAL` (list exactly which
   findings or acceptance criteria are approved; only those may be implemented, the rest are struck through with a
   reason), or `REJECTED` (no conflict with the spec after all, or already fixed; give the evidence).
3. `APPROVED` / `PARTIAL`: implement only the approved part, then hand off as usual.
   `REJECTED`: change no code; move the task to `review` with the evidence in the Hand-off, so the user can close it.
4. If the analysis shows the fix needs a spec decision (the spec is ambiguous or two spec sections disagree), record
   the question under Hand-off → Open questions and ask the user before implementing that part.

## Analysis verdict

APPROVED, 2026-10-20 (worker, recorded by the coordinator). Evidence on main: `CapabilityRegistry.cs:84/95/194-196`
(listAddresses and balance took only mainnet|testnet; listAddresses described as "Algorand addresses");
`BiatecMcpClient.GetCryptoAddressAsync` (`:84`) used only by `BitcoinTransferService:79`; spec
`PRODUCT-DESCRIPTION.md:84,257`. Real MCP schema (sibling BiatecMCP `MCP/BiatecMCP.cs:128-345`, read only):
`getCryptoAddress(network, slot=0, seedAddress?)` → `{network, family, address, error}` (slot 0 = first address,
1 = second); `getCryptoBalance(network, address?, …)` → `{nativeCurrencySymbol, nativeBalance, nativeBalanceBaseUnits
(decimal string), error, errorType}`; failures mostly in `error`, not as tool errors. Real MCP knows only `bitcoin`
(no Bitcoin testnet).

## Goal

Signed-in users can list their Bitcoin addresses and read Bitcoin balances in chat, using the same network names as wallet.transfer.

## Scope

- In: network enum and executors for Bitcoin in wallet.listAddresses and wallet.balance, result shaping (sats and BTC
  decimals), tests with fake MCP.
- Out: the transfer itself (T-024, done), holdings card (T-067).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-087, T-067: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [x] `wallet.listAddresses` with `bitcoin`/`bitcoin-testnet` returns the user's Bitcoin addresses via getCryptoAddress
- [x] `wallet.balance` returns a Bitcoin balance with 8 decimals
- [x] Anonymous sessions still cannot call either tool (tier policy tests stay green)

## Implementation notes

Network names must match `wallet.transfer` exactly. T-087 also edits CapabilityRegistry.cs.

## Verification

`dotnet test --filter "WalletRead|TierPolicy"`.

## Hand-off

- **Summary:** on `bitcoin`, wallet.listAddresses returns the primary (slot 0) and secondary (slot 1) addresses via getCryptoAddress and wallet.balance returns sats (8 decimals, amounts as strings) via getCryptoBalance; one shared `BitcoinOwnAddresses` helper (case-insensitive bech32) backs listAddresses, balance and the Bitcoin transfer ownership check, so the secondary can be a transfer source. `bitcoin-testnet` removed from the shared network enum (real MCP has no Bitcoin testnet; Bitcoin is mainnet only). Algorand wallet.balance fixed against the real MCP shape: MCP network code `algorand-<network>`, native ALGO from `nativeBalanceBaseUnits` plus ASAs, a non-empty `error` is a failed read (`McpToolResult.Failed`).
- **Branch:** `task/T-082` @ `928f497` (merged with main), PR #163 (`Closes #151`).
- **Files changed:** `Api/Wallet/Read/WalletReadExecutors.cs`, tests; outside the area: `Core/Capabilities/CapabilityRegistry.cs`, `Tools/Wallet/Bitcoin/{BitcoinOwnAddresses,BitcoinTransferService}.cs`, `Tools/Mcp/{BiatecMcpClient,McpTypes}.cs`, `Tests/Bitcoin/BitcoinTransferTests.cs`.
- **How it was verified:** WalletRead + TierPolicy + Bitcoin 127 passed; full backend on main + T-025: 1020 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed.
- **Follow-up tasks created:** T-089 (Algorand transfer preview: MCP network code and `error` field).
- **Unblocks:** no task is blocked by T-082.
- **Open questions:** bring back a Bitcoin testnet once Biatec MCP supports one? Only the primary seed's slots 0 and 1 are read (other `seedAddress` seeds are ignored).

## Review

- 2026-10-20 03:33 · approve · PR #163 merged into main @ 4870eff (recorded by kanban-sync)

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #5)
- 2026-10-20 03:01 · LukassSoukup · taken · status stays backlog
- 2026-10-20 03:01 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 03:33 · coordinator · in-progress → review · branch task/T-082 @ 928f497, PR #163; analysis verdict APPROVED recorded
- 2026-10-20 03:33 · user · review → done · PR #163 merged into main @ 4870eff (synced by kanban-sync)
