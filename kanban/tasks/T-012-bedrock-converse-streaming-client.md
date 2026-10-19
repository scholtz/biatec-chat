---
id: T-012
title: Bedrock Converse streaming client
status: review
priority: P0
epic: "Orchestrator"
blocked_by: [T-002]
area: ["backend/Biatec.Chat.Api/Model/**"]
parallel: yes
conflicts_with: []
issue: "#13"
branch: "task/T-012"
owner: "session-20261019-1848"
taken_by: "LukassSoukup"
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

- [x] Unit tests with a fake client cover streaming, tool-use and retry
- [x] cachePoint blocks placed after persona and catalogue
- [x] Manual smoke call with SSO profile hackathon streams text
- [x] maxTokens always present

## Verification

dotnet test --filter Model; AWS_PROFILE=hackathon dotnet run smoke command

## Hand-off

- **Branch:** `task/T-012` @ PR #72. `IModelClient` / `BedrockModelClient` in `backend/Biatec.Chat.Api/Model/`:
  ConverseStream in eu-central-1 with the default credential chain; persona block + cachePoint, catalogue block +
  cachePoint, and a history cachePoint when tools are present; maxTokens always set (a request can only lower it);
  retries throttling/availability/transport errors with equal jitter only while nothing was handed out; one global
  concurrency gate (`Bedrock:MaxConcurrency`); a background pump drains the stream so a slow reader never holds a
  slot; first-event and idle limits; usage is emitted and logged. `ModelMessageStop.Message` is the assistant turn
  to append; run tools only after a `tool_use` stop.
- **Verification:** `dotnet test --filter FullyQualifiedName~Biatec.Chat.Tests.Model` 54 passed; full suite 386
  passed, 1 skipped after merging main. Smoke `AWS_PROFILE=<hackathon profile> dotnet run --project
  backend/Biatec.Chat.Api -- bedrock-smoke` streams text and the second call reads the cache (Haiku by default).
- **Files outside the area:** `Config/Options.cs` (model defaults now Sonnet 4.5 anonymous / Opus 4.5 authenticated
  as the spec says; model-id regex accepts Claude 4.x only), `appsettings.json` (same defaults), `Program.cs`
  (`AddBedrockModel()` and the `bedrock-smoke` dispatch), csproj (AWSSDK.BedrockRuntime, AWSSDK.SSO, AWSSDK.SSOOIDC;
  the SSO packages are needed for any SSO profile, also for the KMS vault).
- **For T-014:** map `ThrottlingException` after exhausted retries and `ModelBusyException` to a "busy" reply.
- **Known limits, left on purpose:** concurrency cap is not a requests-per-minute limiter and there is no reserved
  capacity for signed-in users (T-018); after the stop the pump may hold a slot up to 10 s waiting for usage;
  integers beyond Int64 in tool input are doubles in the history Document (exact in `InputJson`).
- **Open questions:** none.

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:23 · kanban-board · blocked → backlog · blockers done: T-002
- 2026-10-19 18:48 · LukassSoukup · taken · status stays backlog
- 2026-10-19 18:48 · session-20261019-1848 · backlog → in-progress · claimed
- 2026-10-19 20:28 · session-20261019-1848 · in-progress → review · PR #72
