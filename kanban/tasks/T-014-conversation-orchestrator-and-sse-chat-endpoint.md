---
id: T-014
title: Conversation orchestrator and SSE chat endpoint
status: blocked
priority: P0
epic: "Orchestrator"
blocked_by: [T-003, T-005, T-007, T-012, T-013]
area: ["backend/Biatec.Chat.Api/Chat/**", "backend/Biatec.Chat.Api/Orchestration/**"]
parallel: no
conflicts_with: [T-022, T-035, T-049]
issue: "#15"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#chat-backend-biatecchatapi"
created: 2026-10-19
updated: 2026-10-19
---

# T-014 · Conversation orchestrator and SSE chat endpoint

## Context

Backend owns the tool loop; untrusted content wrapped as data in tool results; streaming replies over SSE with tool status lines.

## Goal

POST /chat/messages streaming a full tool-using conversation.

## Scope

- In: Endpoint with CSRF guard, conversation and message persistence, system prompt builder (persona, refusal rules, tier catalogue), loop: model -> gate -> executor -> result wrapped as untrusted -> model, SSE events per chat-protocol, executor interface for later tools.
- Out: Specific tools (T-015, T-019+).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-022, T-035, T-049: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Text streams as SSE delta events
- [ ] A tool call runs through the gate and executor and the result reaches the model
- [ ] Messages persisted with redaction hook
- [ ] Integration test with fake Bedrock

## Verification

dotnet test --filter Orchestration; curl -N POST /chat/messages

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
