---
id: T-032
title: safe.create and safe.list capabilities
status: backlog
priority: P1
epic: "Safe"
blocked_by: [T-022, T-031]
area: ["backend/Biatec.Chat.Tools/Safe/Create/**"]
parallel: no
conflicts_with: [T-036, T-038, T-060]
issue: "#33"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-19
updated: 2026-10-19
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

Parallel work possible: **no**. Don't run it at the same time as T-036, T-038, T-060: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] 2-of-2 safe created on testnet from chat
- [ ] App id and safe address shown
- [ ] safe.list shows pending proposals with progress
- [ ] Tests with fakes

## Verification

dotnet test --filter SafeCreate

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-031
