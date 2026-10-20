---
id: T-082
title: "Bug: wallet.listAddresses and wallet.balance must cover Bitcoin"
status: backlog
priority: P1
epic: "Wallet tools"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Wallet/Read/**", "backend/Biatec.Chat.Tests/Wallet/**", "backend/Biatec.Chat.Core/Capabilities/CapabilityRegistry.cs"]
parallel: no
conflicts_with: [T-087, T-067]
issue: "#151"
branch: ""
owner: ""
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

_pending: filled in by the agent that runs the Analysis prompt (verdict, date, evidence, approved items)._

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

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] `wallet.listAddresses` with `bitcoin`/`bitcoin-testnet` returns the user's Bitcoin addresses via getCryptoAddress
- [ ] `wallet.balance` returns a Bitcoin balance with 8 decimals
- [ ] Anonymous sessions still cannot call either tool (tier policy tests stay green)

## Implementation notes

Network names must match `wallet.transfer` exactly. T-087 also edits CapabilityRegistry.cs.

## Verification

`dotnet test --filter "WalletRead|TierPolicy"`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-082`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #5)
- 2026-10-20 03:01 · LukassSoukup · taken · status stays backlog
