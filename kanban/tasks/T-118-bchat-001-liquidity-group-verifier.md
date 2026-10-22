---
id: T-118
title: "Verify liquidity groups from the builder sidecar field by field (BCHAT-001)"
status: done
priority: P1
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Tools/Dex/Liquidity/**", "backend/Biatec.Chat.Api/Dex/Liquidity/**", "backend/Biatec.Chat.Tests/Dex/Liquidity/**"]
parallel: yes
conflicts_with: []
issue: "#254"
branch: "task/T-118"
owner: "session-20261022-1600"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-118 - Verify liquidity groups from the builder sidecar field by field (BCHAT-001)

## Context

Security audit finding BCHAT-001 (High, issue #254, `audits/reports/20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01.md`).
`LiquidityBuilder.BuildAsync` decodes the sidecar's group and checks senders and the sidecar's *facts*, but never
inspects `RekeyTo`, `CloseRemainderTo`, `AssetCloseTo`, receivers, amounts, app ids or fee in the transactions that are
actually signed. Swaps (`SwapGroupVerifier`), transfers, safes and x402 already verify fields. The spec requires
verifying builders independently (section 7.2).

## Goal

A liquidity group whose transactions do anything other than the requested add/remove is rejected with
`LiquidityBuildException` before a preview row is persisted.

## Scope

- In: a `LiquidityGroupVerifier` mirroring `SwapGroupVerifier`: all transactions from the user; no rekey, close,
  clawback, keyreg, or app create/update/delete/clear; transfers only to the pool application address with exactly
  amountA/amountB (plus an optional zero-amount LP opt-in to self); app calls only to the pool app and the network's
  pool-provider app; re-encode equality as in `X402PayService.RequireNoHiddenFields`; reuse for the safe liquidity
  payload. Unit tests.
- Out: the fee ceiling (BCHAT-003, a separate task); pinning safe deployment bytes (BCHAT-010).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Add and remove liquidity groups are verified by a `LiquidityGroupVerifier` before the preview is persisted.
- [x] A group with `rekeyTo`, `closeRemainderTo`, `assetCloseTo`, an extra outflow, a wrong app id, a wrong receiver or
      wrong amount is rejected with `LiquidityBuildException`, and no preview row exists.
- [x] The honest group still produces a preview (existing liquidity tests stay green).
- [x] `audits/harness/.../TEST-008.sh` passes.

## Implementation notes

Reference: `SwapGroupVerifier.cs:23-27`, `TransferStepBuilder.cs:109-120`, `SafeBuilder.cs:217`,
`SafeProposalBuilder.cs:218`, `X402PayService.cs:112`. Pool address is `Address.ForApplication(poolAppId)`;
provider app from `BiatecDexApps.For(network)`.

## Verification

`dotnet test backend --filter Liquidity`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-008.sh`.

## Hand-off

- **Summary:** LiquidityGroupVerifier runs on the decoded transactions in LiquidityBuilder before any preview; checked against the listed pool (not builder facts). Branch task/T-118 @ b140d3a, PR #300.
- **Files changed:** Tools/Dex/Liquidity (verifier, builder), Api/Dex/Liquidity/LiquidityExecutors.cs (input validation), Tests/Dex/Liquidity.
- **How it was verified:** dotnet test (1517+ passed), TEST-008 passes, CI green. Three review rounds; remaining findings are the fee cap (BCHAT-003) and deeper pool-call arg checks (hardening, not in this finding).
- **Follow-up tasks created:** none (BCHAT-003 already exists as #256).
- **Unblocks:** none.
- **Open questions:** none.

## Review

- 2026-10-22 18:12 · approve · PR #300 merged into main @ 4c7803b (recorded by kanban-sync)

## Activity log

- 2026-10-22 16:00 · claude · (new) → backlog · created from GitHub issue #254 (audit finding BCHAT-001); blockers scan: none
- 2026-10-22 16:01 · scholtz · taken · status stays backlog
- 2026-10-22 16:02 · session-20261022-1600 · backlog → in-progress · claimed by scholtz
- 2026-10-22 18:12 · user · in-progress → done · PR #300 merged into main @ 4c7803b (synced by kanban-sync)
