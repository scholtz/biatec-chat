---
id: T-013
title: Capability registry and tier gate
status: in-progress
priority: P0
epic: "Orchestrator"
blocked_by: [T-001]
area: ["backend/Biatec.Chat.Core/Capabilities/**", "backend/Biatec.Chat.Tests/Capabilities/**"]
parallel: yes
conflicts_with: []
issue: "#14"
branch: "task/T-013"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-013 · Capability registry and tier gate

## Context

Each capability declares minimum tier, scopes, moves-funds and valuation; anonymous tool list contains only requestSignIn and kb.search; every tool call is checked server-side against tier, scopes and registry plus JSON schema.

## Goal

Single source of truth for what the model may call.

## Scope

- In: Capability records for the initial set (x402.pay behind a flag), tool list per tier and scope, gate with JSON schema validation returning a model-facing error and a policy event.
- Out: Executors themselves.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Anonymous tool list is exactly requestSignIn and kb.search
- [ ] Gate rejects a wallet tool call in anonymous tier
- [ ] Gate rejects schema-invalid arguments
- [ ] Policy event emitted on every rejection

## Verification

dotnet test --filter Capabilities

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:21 · kanban-board · blocked → backlog · blockers done: T-001
- 2026-10-19 19:05 · scholtz · taken · status stays backlog
- 2026-10-19 19:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
