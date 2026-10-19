---
id: T-026
title: limits.view and lowering-only limits.update
status: done
priority: P2
epic: "Wallet tools"
blocked_by: [T-022]
area: ["backend/Biatec.Chat.Tools/Limits/**"]
parallel: yes
conflicts_with: []
issue: "#27"
branch: "task/T-026"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-20
---

# T-026 · limits.view and lowering-only limits.update

## Context

limits.view via GET /wallet/limits; raising limits needs consent-gated OIDC changes that don't exist, so the fallback is lowering-only; hidden when tenant lacks manage-limits.

## Goal

Users can see and reduce their limits from chat.

## Scope

- In: Read executor, lowering-only update executor with preview (limits change hash), manage-limits elevation, tenant flag.
- Out: Raising limits.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Limits shown with currency
- [ ] Lowering applies after confirmation
- [ ] Any raise is refused with explanation
- [ ] Hidden when tenant flag off

## Verification

dotnet test --filter Limits

## Hand-off

## Review

- 2026-10-20 01:01 · approve · PR #117 merged into main @ bbf6f5e (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 01:01 · user · in-progress → done · PR #117 merged into main @ bbf6f5e (synced by kanban-sync)
