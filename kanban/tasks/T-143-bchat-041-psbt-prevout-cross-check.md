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

- **Summary:** IBitcoinChainWatcher gets GetOutputsAsync (Esplora tx/{txid}: every output's value and scriptpubkey); BitcoinTransferService.BuildAsync looks up each distinct funding transaction once and refuses a PSBT unless every input's value and script equal the chain's, or when the transaction is unknown or the chain cannot be read. The Esplora client buffers at most 4 MiB. Without a watcher the PSBT's own data stands, as before.
- **Files changed:** Tools/Wallet/Bitcoin/BitcoinTransferService.cs, Api/Wallet/Transfer/BitcoinPreviewFreshness.cs, Tests/Bitcoin/BitcoinTransferTests.cs.
- **How it was verified:** Bitcoin tests 28 passed (wrong value, wrong script, only the second input wrong, each funding transaction looked up once, unknown/unreadable refused, no watcher keeps old behaviour; Esplora parsing incl. non-numeric value, missing script, not JSON, endpoint down, 404, non-txid, unknown network). Full backend suite run before merge.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** Dormant while Bitcoin transfers are off (T-141). Unknown and unreadable are one answer (null) and give one message; an input spending an unconfirmed output may not be served by Esplora and would be refused (fail closed). It does not check that the output is unspent (the freshness gate does that at Confirm).

## Review

## Activity log

- 2026-10-24 00:10 · claude · (new) → backlog · created from GitHub issue #294 (BCHAT-041); blockers scan: none
- 2026-10-23 14:43 · scholtz · taken, and session-20261023-1443 · backlog → in-progress · claimed
