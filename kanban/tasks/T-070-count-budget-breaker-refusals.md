---
id: T-070
title: Count budget-breaker refusals in the refusals metric
status: review
priority: P3
epic: "Deploy & docs"
blocked_by: [T-018, T-057]
area: ["backend/Biatec.Chat.Api/Budget/**", "backend/Biatec.Chat.Tests/Budget/**"]
parallel: yes
conflicts_with: []
issue: "#125"
branch: "task/T-070"
owner: "session-20261020-0026"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#non-functional-requirements"
created: 2026-10-20
updated: 2026-10-20
---

# T-070 · Count budget-breaker refusals in the refusals metric

## Context

Follow-up from T-057's hand-off. The spec's Observability requirement asks for "metrics for refusals per tier".
T-057's `biatec.chat.refusals` counts gate refusals and `requestSignIn` calls, but not the refusals T-018's
`BudgetedModelClient` gives when a Bedrock budget is used up, which is exactly when refusals spike.

## Goal

Every budget-breaker refusal shows up in `biatec.chat.refusals` with its tier and reason `budget_exhausted`.

## Scope

- In: `BudgetedModelClient` takes the optional `ChatMetrics` (registered only when telemetry is on) and counts the
  anonymous refusal and the authenticated `BudgetExhaustedException`; a cached answer is not a refusal.
- Out: other metrics.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] Anonymous refusal past the anonymous share counts `{tier=anonymous, reason=budget_exhausted}`
- [x] Authenticated refusal past the full budget counts `{tier=authenticated, reason=budget_exhausted}`
- [x] A cached answer is not counted; with telemetry off nothing changes

## Verification

dotnet test --filter Budget

## Hand-off

- **Summary:** `BudgetedModelClient` takes the optional `ChatMetrics` and counts `biatec.chat.refusals{reason=budget_exhausted}` per tier; cached answers aren't counted. `BudgetRegistration` resolves `ChatMetrics` with `GetService` (present only when telemetry is on).
- **Branch:** `task/T-070` @ `8889f04`, PR #127 (`Closes #125`).
- **Files changed:** `Budget/BudgetedModelClient.cs`, `Budget/BudgetRegistration.cs`, `Tests/Budget/BudgetTests.cs`.
- **How it was verified:** `dotnet test --filter Budget` 21/21; full suite 567 passed, 1 skipped (KMS), 0 failed; code review (low): no findings.
- **Follow-up tasks created:** none.
- **Unblocks:** no task is blocked by T-070.
- **Open questions:** none.

## Review

## Activity log

- 2026-10-20 00:40 · LukassSoukup · (new) → backlog · follow-up from T-057's hand-off; blockers T-018, T-057 (done); no file overlap with unfinished tasks
- 2026-10-20 00:26 · session-20261020-0026 · backlog → in-progress · claimed by /kanban-next
- 2026-10-20 00:30 · session-20261020-0026 · in-progress → review · PR #127, tests and review passed
