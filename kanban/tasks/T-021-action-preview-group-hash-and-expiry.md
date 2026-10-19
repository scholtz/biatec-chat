---
id: T-021
title: Action preview, group hash and expiry
status: done
priority: P0
epic: "Wallet tools"
blocked_by: [T-003, T-004, T-005]
area: ["backend/Biatec.Chat.Core/Previews/**", "backend/Biatec.Chat.Tools/Algod/**"]
parallel: yes
conflicts_with: []
issue: "#22"
branch: "task/T-021"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-19
---

# T-021 · Action preview, group hash and expiry

## Context

Every fund-moving build yields a structured preview bound to the group hash (Algorand group id; assign grp to a lone txn via SDK); algod simulate; expiry from lastValid; Confirm disabled under 600 s; expired preview is rebuilt with a new hash; multi-step actions build later steps after earlier ones land.

## Goal

Shared preview machinery all capabilities use.

## Scope

- In: ActionPreview/ActionStep service, Algorand group id computation and grp assignment using the Algorand .NET SDK, simulate client per network, expiry and countdown fields, rebuild linkage (rebuiltAsPreviewId), multi-step state, preview DTO.
- Out: Bitcoin specifics (T-024), signing (T-022).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Group hash equals SDK group id for test vectors
- [ ] Lone transaction gets grp without other field changes
- [ ] Preview carries expiry; confirm refused under 600 s
- [ ] Rebuild produces a new preview linked to the old

## Verification

dotnet test --filter Previews

## Hand-off

## Review

- 2026-10-19 19:32 · approve · PR #80 merged into main @ 82672c3 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:41 · kanban-board · blocked → backlog · blockers done: T-003, T-004, T-005
- 2026-10-19 19:05 · scholtz · taken · status stays backlog
- 2026-10-19 19:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 19:32 · user · in-progress → done · PR #80 merged into main @ 82672c3 (synced by kanban-sync)
