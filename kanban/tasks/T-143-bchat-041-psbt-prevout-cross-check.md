---
id: T-143
title: "Cross-check PSBT input values against the chain before previewing (BCHAT-041)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Tools/Wallet/Bitcoin/BitcoinTransferService.cs", "backend/Biatec.Chat.Api/Wallet/Transfer/BitcoinPreviewFreshness.cs", "backend/Biatec.Chat.Tests/Bitcoin/**"]
parallel: yes
conflicts_with: []
issue: "#294"
branch: "task/T-143"
owner: "session-20261023-1443"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-24
---

# T-143 - Cross-check PSBT input values against the chain before previewing (BCHAT-041)

## Context

Security audit finding BCHAT-041 (Low, issue #294; unreachable by default since T-141 / BCHAT-037). The Bitcoin preview shows the fee as inputs minus outputs. An input
that carries only `witness_utxo` has a value and script the builder (Biatec MCP) asserts; a wrong value makes the displayed fee wrong. The PSBT decoder
verifies `non_witness_utxo` against the spent txid, but `witness_utxo` is taken as given.

## Goal

A Bitcoin preview is built only from input values the chain confirms.

## Scope

- In: `IBitcoinChainWatcher.GetPrevoutAsync` (Esplora `tx/{txid}`), a check in `BitcoinTransferService.BuildAsync` that every decoded input's value and script equal the chain's, refusing on a mismatch or when the chain cannot be read; tests.
- Out: the PSBT signing path; mempool (unconfirmed) outputs beyond what Esplora serves.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] An input whose value or script differs from the chain's makes the build refuse; so does a prevout the chain does not know or cannot be read.
- [x] A PSBT whose inputs match the chain is previewed as before.
- [x] The Esplora watcher parses `vout[n].value` and `scriptpubkey` correctly and treats a transport error as unknown.
- [x] The full backend suite stays green.

## Verification

`dotnet test backend`.

## Hand-off

- **Summary:** IBitcoinChainWatcher gets GetPrevoutAsync (Esplora tx/{txid}: vout value and scriptpubkey); BitcoinTransferService.BuildAsync refuses a PSBT unless every input's value and script equal what the chain reports for the output it spends, and refuses an output the chain does not know or cannot be read. Without a watcher the PSBT's own data stands, as before.
- **Files changed:** Tools/Wallet/Bitcoin/BitcoinTransferService.cs, Api/Wallet/Transfer/BitcoinPreviewFreshness.cs, Tests/Bitcoin/BitcoinTransferTests.cs.
- **How it was verified:** Bitcoin tests 25 passed (wrong value, wrong script, unknown or unreadable output all refused; matching inputs preview as before; the Esplora parsing, missing output, 404, non-txid and unknown network). Full backend suite run before the PR.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** Bitcoin transfers are off by default (T-141), so this is dormant until Wallet__BitcoinTransfers is enabled. An input spending an unconfirmed output may not be served by Esplora and would be refused (fail closed). The check needs the chain watcher registered, which it always is; a service built without one skips it.

## Review

## Activity log

- 2026-10-24 00:10 · claude · (new) → backlog · created from GitHub issue #294 (BCHAT-041); blockers scan: none
- 2026-10-23 14:43 · scholtz · taken, and session-20261023-1443 · backlog → in-progress · claimed
