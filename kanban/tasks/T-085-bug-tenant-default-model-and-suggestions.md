---
id: T-085
title: "Bug: apply the tenant's defaultModel and suggestions"
status: backlog
priority: P3
epic: "Orchestrator"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Orchestration/ConversationOrchestrator.cs", "backend/Biatec.Chat.Api/Model/ModelRegistration.cs", "backend/Biatec.Chat.Tests/Orchestration/**", "packages/chat-widget/src/components/chat/suggestions.ts"]
parallel: no
conflicts_with: [T-067, T-076]
issue: "#154"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#data-model-backend"
created: 2026-10-20
updated: 2026-10-20
---

# T-085 · Bug: apply the tenant's defaultModel and suggestions

## Context

Spec, Data Model: `Tenant` has `defaultModel` and `suggestions`. Embedding Modes: the widget *"falls back to that
tenant's own backend ... so the tenant's model, suggestions, and evidence store still apply"*. Model Layer: *"Model
identifiers are configuration, not code."* Widget option `suggestions: 'default'`.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #8).

- `Tenant.DefaultModel` and `Tenant.Suggestions` (`Core/Data/Entities.cs:213-214`) are never read
  (`git grep DefaultModel` finds only the entity and migrations).
- The model is always `Bedrock:AuthenticatedModelId` / `AnonymousModelId` from global config; sample prompts are
  hard-coded in `packages/chat-widget/src/components/chat/suggestions.ts`.
- Fix: per-tenant model override validated against the same `eu.anthropic.claude-*-4-*` rule (never Claude 5),
  budgets still priced by model; suggestions delivered to the widget (session response or a public endpoint) when the
  widget option is `'default'`.
- Low risk, low demo value: confirm with the user that it is worth doing before the freeze.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-085 in biatec-chat. Read the Tenant row of the Data Model, Embedding Modes (tenant fallback) and Model
Layer in PRODUCT-DESCRIPTION.md. Grep for DefaultModel and Suggestions across backend/ and packages/. Decide whether
defaultModel overrides the authenticated model only or both tiers (spec says Sonnet for anonymous discovery), how
suggestions reach the widget on both transports, and whether a chat-protocol change is needed (which would conflict
with T-074).
>
> Report: (a) does the conflict still exist on current `main`, with file:line evidence; (b) is the spec text
> unambiguous, quoting it; (c) the smallest change that satisfies it and what it touches outside this task's `area`;
> (d) risks and tests needed. End with a verdict line: `APPROVED`, `PARTIAL: <approved items>` or
> `REJECTED: <reason>`, and write it into the task's Analysis verdict section.

## Analysis gate

This task starts with an analysis step. **No code may be written until the Analysis verdict below says `APPROVED`
or `PARTIAL`.**

1. Run the **Analysis prompt** above against the current `main` (the finding was recorded on `main @ 6b701e5`;
   later merges may already have fixed or changed it).
2. Write the result into **Analysis verdict**: `APPROVED` (implement as scoped), `PARTIAL` (list exactly which
   findings or acceptance criteria are approved; only those may be implemented, the rest are struck through with a
   reason), or `REJECTED` (no conflict with the spec after all, or already fixed; give the evidence).
3. `APPROVED` / `PARTIAL`: implement only the approved part, then hand off as usual.
   `REJECTED`: change no code; move the task to `review` with the evidence in the Hand-off, so the user can close it.
4. If the analysis shows the fix needs a spec decision (the spec is ambiguous or two spec sections disagree), record
   the question under Hand-off → Open questions and ask the user before implementing that part.

## Analysis verdict

_pending: filled in by the agent that runs the Analysis prompt (verdict, date, evidence, approved items)._

## Goal

A tenant's configured model and sample prompts are used instead of the global defaults when set.

## Scope

- In: model selection by tenant, suggestions delivery and widget use, tests.
- Out: tenant admin UI (T-086).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-067, T-076: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] A tenant with `defaultModel` set gets that model id in the Bedrock request (fake model client)
- [ ] An invalid or Claude 5 model id is rejected when the tenant is saved
- [ ] The widget shows the tenant's suggestions when set, the built-in ones otherwise

## Implementation notes

If a protocol change is needed, add T-074 to conflicts_with on both sides.

## Verification

`dotnet test --filter Orchestration`; `pnpm --filter @biatec/chat-widget test`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-085`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #8)
- 2026-10-20 05:53 · coordinator · backlog · conflicts_with + T-067 (both edit ConversationOrchestrator.cs; seen on task/T-067)
