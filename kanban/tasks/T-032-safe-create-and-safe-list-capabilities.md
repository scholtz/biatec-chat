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
- [ ] App id and safe address shown
- [ ] safe.list shows pending proposals with progress
- [ ] Tests with fakes

## Verification

dotnet test --filter SafeCreate

## Hand-off

## Review

- 2026-10-20 01:57 · approve · PR #135 merged into main @ 66101a2 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-031
- 2026-10-20 01:09 · LukassSoukup · backlog · parallel analysis: dropped T-038 (done)
- 2026-10-20 01:12 · LukassSoukup · taken · status stays backlog
- 2026-10-20 01:13 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 01:57 · user · in-progress → done · PR #135 merged into main @ 66101a2 (synced by kanban-sync)
