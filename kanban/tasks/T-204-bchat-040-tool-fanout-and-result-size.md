---
id: T-204
title: "Cap tool calls per model turn and the size of a tool result (BCHAT-040)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Orchestration/ConversationOrchestrator.cs", "backend/Biatec.Chat.Tests/Orchestration/**"]
parallel: yes
conflicts_with: []
issue: "#293"
branch: "task/T-204"
owner: "session-20261023-1240"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-204 - Cap tool calls per model turn and the size of a tool result (BCHAT-040)

## Context

Security audit finding BCHAT-040 (Low, issue #293, FIN-9). `ConversationOrchestrator` caps the rounds of the tool loop (`MaxToolRounds`) but not how many tool calls the model makes in one round, nor how large a tool result may be. A model (or an injected prompt) can ask for dozens of calls in one answer, and one executor can return megabytes that are stored, recorded as evidence and sent back to the model.

## Goal

At most `MaxToolUsesPerRound` calls run in one round (the rest are answered with a refusal, so the conversation stays well-formed), and a result above `MaxToolResultBytes` is replaced by an error telling the model to ask for less.

## Scope

- In: the two caps and their tests.
- Out: per-tool budgets; the model-side `maxTokens` (already set).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] A round with more than `MaxToolUsesPerRound` tool uses executes only the first ones; every tool use still gets a result block, the surplus ones an error.
- [ ] A tool result larger than `MaxToolResultBytes` UTF-8 bytes is not stored, recorded or sent as is: the model gets an error result naming the limit.
- [ ] Existing orchestration tests stay green.

## Verification

`dotnet test backend --filter Orchestration`, full suite in CI.

## Hand-off

## Review

## Activity log

- 2026-10-23 12:40 · kanban-split · (new) → backlog · created from audit issue #293
- 2026-10-23 12:40 · scholtz · taken · status stays backlog
- 2026-10-23 12:40 · session-20261023-1240 · backlog → in-progress · claimed, branch task/T-204
