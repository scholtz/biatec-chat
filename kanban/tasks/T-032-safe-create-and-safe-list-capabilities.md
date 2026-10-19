---
id: T-032
title: safe.create and safe.list capabilities
status: done
priority: P1
epic: "Safe"
blocked_by: [T-022, T-031]
area: ["backend/Biatec.Chat.Tools/Safe/Create/**"]
parallel: no
conflicts_with: [T-036, T-060]
issue: "#33"
branch: "task/T-032"
owner: "worker-T-032"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-19
updated: 2026-10-20
---

# T-032 · safe.create and safe.list capabilities

## Context

Create a safe with name, network and initial signer group collected in chat; list safes, pending proposals, progress, epoch and expiry.

## Goal

Step 1 and 3 of the demo script.

## Scope

- In: Executors, address collection prompts, preview (creation valued zero), safe card data.
- Out: Proposals (T-033).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036, T-060: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] 2-of-2 safe created on testnet from chat
- [x] App id and safe address shown
- [x] safe.list shows pending proposals with progress
- [x] Tests with fakes

## Verification

dotnet test --filter SafeCreate

## Hand-off

- **Summary:** `safe.create` as a zero-valued two-step preview (deploy via algo-safe in the sidecar; app id recorded when the deploy lands; funding check against max(floor, min balance); bootstrap + finalize of one "Signers" group), resuming a landed deploy instead of redeploying; `safe.list` safe card data (safes, member addresses, signer groups with epoch, pending proposals with N of M, payload version, expiry, approvedByYou across all user addresses; unreadable safes reported as such). Discovery via the new `KnownSafes` table, safes created in chat and named app ids. Transaction bytes are decoded (`SafeAbi`) and checked against the request before signing.
- **Branch:** `task/T-032` @ `4bc50bb` (merged with main), PR #135 (`Closes #33`). Worker in its own worktree; coordinator re-ran everything on the merge with current main.
- **Files outside the area:** `Program.cs`, `CapabilityRegistry` (input schemas), `BuildersClient`, `Entities`/`ChatDbContext` + migration `KnownSafes`; confirm path (additions, optional): `TxConfirmation.CreatedAppId`, `AlgodTransactionConfirmer`, `IStepLandedObserver` in `SettlementHandler.cs`, `ConfirmService` calls observers after a step lands; sidecar (T-031's code): group-id bug fix for multi-transaction safe groups, `membershipEpoch` in safe.list.
- **Verification:** backend 902 passed, 1 skipped, 0 failed (SafeCreate 18); sidecar 55 passed, 1 skipped; merges cleanly with `task/T-049`.
- **Review (high):** fixed redeploy after a failed setup, lost app id, funding asked on unrelated simulation failures, approvedByYou with several addresses, all-or-nothing safe.list, threshold overflow, bootstrap check trusting the description over the bytes. Known limitations: newest 50 known safes per tenant; sequential sidecar calls; duplicated algod/indexer lookup; resume only within the same conversation.
- **Not met:** 2-of-2 safe created on live testnet with a phone approval (needs live testnet; covered by T-059's end-to-end run). The 0.5 ALGO funding floor is an estimate to check there.
- **Open questions:** is tenant-scoped discovery (KnownSafes) plus named app ids enough for Bob to find Alice's safe?
- **Follow-up tasks created:** T-074 (widget safe card).
- **Unblocks:** T-033 becomes ready once this is done.

## Review

- 2026-10-20 01:57 · approve · PR #135 merged into main @ 66101a2 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-031
- 2026-10-20 01:09 · LukassSoukup · backlog · parallel analysis: dropped T-038 (done)
- 2026-10-20 01:12 · LukassSoukup · taken · status stays backlog
- 2026-10-20 01:13 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 01:56 · coordinator · in-progress → review · branch task/T-032 @ 4bc50bb, PR #135
- 2026-10-20 01:57 · user · in-progress → done · PR #135 merged into main @ 66101a2 (synced by kanban-sync)
