---
id: T-012
title: Bedrock Converse streaming client
status: backlog
priority: P0
epic: "Orchestrator"
blocked_by: [T-002]
area: ["backend/Biatec.Chat.Api/Model/**"]
parallel: yes
conflicts_with: []
issue: "#13"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#model-layer"
created: 2026-10-19
updated: 2026-10-19
---

# T-012 · Bedrock Converse streaming client

## Context

Model calls via Bedrock from the backend only; model ids are configuration; system prompt = persona+refusal rules, then tier catalogue, cached; volatile data only in tool results.

## Goal

A reusable ConverseStream client with caching, retries and limits.

## Scope

- In: AWSSDK.BedrockRuntime ConverseStream in eu-central-1, default credential chain, eu. Claude 4.5 ids (Opus 4.5 authenticated, Sonnet 4.5 anonymous), maxTokens always set, cachePoint after each static system block, tool config, adaptive retry with jitter on ThrottlingException, global concurrency cap, usage capture.
- Out: Tool loop (T-014), guardrails (T-016).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Unit tests with a fake client cover streaming, tool-use and retry
- [ ] cachePoint blocks placed after persona and catalogue
- [ ] Manual smoke call with SSO profile hackathon streams text
- [ ] maxTokens always present

## Verification

dotnet test --filter Model; AWS_PROFILE=hackathon dotnet run smoke command

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:23 · kanban-board · blocked → backlog · blockers done: T-002
