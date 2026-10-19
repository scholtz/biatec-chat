---
id: T-035
title: Record evidence across chat, previews and signing
status: in-progress
priority: P1
epic: "Evidence"
blocked_by: [T-022, T-034]
area: ["backend/Biatec.Chat.Api/Evidence/Recording/**"]
parallel: no
conflicts_with: [T-014, T-015, T-016, T-024, T-025, T-049, T-057]
issue: "#36"
branch: "task/T-035"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#evidence-record"
created: 2026-10-19
updated: 2026-10-19
---

# T-035 · Record evidence across chat, previews and signing

## Context

Record session metadata, messages, tool calls and results (schema redaction), previews and steps with hashes, confirmations, consent requests and receipts, signing outcomes, tx ids and status.

## Goal

Every action leaves a trail.

## Scope

- In: Recording hooks in orchestrator, preview, confirm, signing and submit paths; schema-driven redaction of fields marked sensitive.
- Out: Export (T-036).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-014, T-015, T-016, T-024, T-025, T-049, T-057: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] A transfer conversation produces entries of every listed kind
- [ ] Sensitive fields redacted by schema
- [ ] No IPs recorded
- [ ] Tests

## Verification

dotnet test --filter EvidenceRecording

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-034
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 23:24 · LukassSoukup · in-progress · conflicts_with revised: dropped T-018 (budget lives in an IModelClient decorator), T-057 kept
