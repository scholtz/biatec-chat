---
id: T-018
title: Bedrock budget and circuit breaker
status: done
priority: P2
epic: "Orchestrator"
blocked_by: [T-014]
area: ["backend/Biatec.Chat.Api/Budget/**"]
parallel: no
conflicts_with: [T-057]
issue: "#19"
branch: "task/T-018"
owner: "session-20261019-2341"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#non-functional-requirements"
created: 2026-10-19
updated: 2026-10-19
---

# T-018 · Bedrock budget and circuit breaker

## Context

Bedrock spend capped per tenant and globally; breaker degrades anonymous tier to cached answers and a refusal; authenticated sessions keep reserved capacity.

## Goal

A runaway client cannot exhaust the shared budget.

## Scope

- In: Usage-to-cost accounting from Converse usage, per-tenant and global budgets in config, Redis counters, breaker behaviour for anonymous tier, reserved share for authenticated.
- Out: Billing APIs.
- Constraint: implement as a decorator around `IModelClient` registered in `ModelRegistration`; don't edit `ConversationOrchestrator` (T-035 adds its recording hooks there).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-057: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] Cost accrues from usage per tenant
- [x] Exhausted anonymous budget returns cached/refusal reply without a Bedrock call
- [x] Authenticated sessions still served within reserve
- [x] Tests with fake usage

## Verification

dotnet test --filter Budget

## Hand-off

- **Branch:** `task/T-018` @ `58c4a21`, PR #122 (`Closes #19`).
- **What:** `backend/Biatec.Chat.Api/Budget/`: `BudgetedModelClient` decorates `IModelClient` (`AddBedrockBudget`, called after `AddBedrockModel`); `RedisBudgetLedger` (micro-USD per tenant and global per fixed window), `RedisAnswerCache` (question hashed with the tenant), `BudgetOptions` + EU model prices. `ConversationOrchestrator` is untouched.
- **Behaviour:** anonymous sessions stop at (1 − `ReservedAuthenticatedShare`) of either budget and get the cached answer to the same single question, else a refusal (with a sign-in hint while authenticated capacity remains), without a Bedrock call. Authenticated sessions use the full budget, then `BudgetExhaustedException` surfaces as the orchestrator's existing `model_unavailable` error. Per-tenant cap from `Tenant.BedrockBudgets` `{"usdPerWindow": n}`.
- **Outside the area:** one line in `Program.cs`, a `Budget` section in `appsettings.json` (both append-only), row 23 of `docs/threat-model.md`.
- **Verification:** `dotnet test --filter Budget` 19/19 passed (Redis tests ran against the local compose Redis); full suite 555 passed, 1 skipped (KMS), 0 failed.
- **Residual risk:** the cap is checked before a call, so concurrent calls can overshoot by their own cost; Redis errors fail open; usage of a call cancelled before Bedrock's usage event isn't counted.
- **Open questions:** defaults (20 USD/day global, 5 USD/day per tenant, 25 % reserved) are guesses sized to the hackathon budget; adjust in config.
- **Unblocks:** no task is blocked by T-018. T-057 (taken by LukassSoukup) no longer conflicts once T-018 leaves in-progress, but still waits on T-035.

## Review

- 2026-10-19 23:57 · approve · PR #122 merged into main @ fed4d80 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-014
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped T-016 (done) and T-035: budget accounting and breaker go in an IModelClient decorator, not the orchestrator
- 2026-10-19 23:25 · LukassSoukup · taken · status stays backlog
- 2026-10-19 23:41 · session-20261019-2341 · backlog → in-progress · claimed by /kanban-next
- 2026-10-19 23:51 · session-20261019-2341 · in-progress → review · PR #122, verification passed
- 2026-10-19 23:57 · user · review → done · PR #122 merged into main @ fed4d80 (synced by kanban-sync)
