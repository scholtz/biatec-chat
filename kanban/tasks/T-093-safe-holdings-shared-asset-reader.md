---
id: T-093
title: "Safe proposal card: use the shared algod asset reader"
status: done
priority: P3
epic: "Safe"
blocked_by: [T-090]
area: ["backend/Biatec.Chat.Api/Safe/Proposals/**", "backend/Biatec.Chat.Tools/Algod/**"]
parallel: yes
conflicts_with: []
issue: "#180"
branch: "task/T-093"
owner: "worker-T-093"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-093 · Safe proposal card: use the shared algod asset reader

## Context

Proposed by the T-090 worker. `AlgodSafeHoldingsReader` in `Api/Safe/Proposals/SafeProposalExecutors.cs` has its own uncached copy of the `/v2/assets/{id}` and `/v2/accounts/{addr}/assets/{id}` reads; T-090 added the shared, cached `IAlgodAssetReader` (Tools/Algod) with unit-name sanitizing. The two key networks differently (`Algod:Networks` names vs `algorand-<name>`).

## Goal

One algod asset reader (cache, sanitizing) for transfers and safe proposal cards.

## Scope

- In: make AlgodSafeHoldingsReader delegate to IAlgodAssetReader, reconciling the network keys; no behaviour change on the card.
- Out: new card fields.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] Safe proposal cards read decimals and unit names through IAlgodAssetReader (test)
- [x] SafeProposal tests stay green

## Verification

`dotnet test --filter SafeProposal`, then the full backend suite.

## Hand-off

- **Summary:** `AlgodSafeHoldingsReader` delegates to the shared `IAlgodAssetReader` (one cache, unit-name cleaning, strict 404 handling); the extra named HttpClient is gone. `AddSafeProposalTools` now relies on `AddWalletTransferTools` (already called first in Program.cs).
- **Branch:** `task/T-093` @ `0590408`, PR #194 (`Closes #180`).
- **Verification:** SafeProposal 25 passed (new SafeProposalHoldingsReaderTests); full backend 1195 passed, 1 skipped, 0 failed.
- **Unblocks:** no task is blocked by T-093.

## Review

- 2026-10-20 05:47 · approve · PR #194 merged into main @ f7deaa1 (recorded by kanban-sync)

## Activity log

- 2026-10-20 04:16 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 04:17 · kanban-board · backlog → blocked · waiting on: T-090
- 2026-10-20 04:31 · kanban-board · blocked → backlog · blockers done: T-090
- 2026-10-20 05:32 · LukassSoukup · taken · status stays backlog
- 2026-10-20 05:32 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 05:47 · coordinator · in-progress → review · PR #194
- 2026-10-20 05:47 · user · review → done · PR #194 merged into main @ f7deaa1 (synced by kanban-sync)
