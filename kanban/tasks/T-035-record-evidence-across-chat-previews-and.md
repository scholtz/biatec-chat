---
id: T-035
title: Record evidence across chat, previews and signing
status: blocked
priority: P1
epic: "Evidence"
blocked_by: [T-022, T-034]
area: ["backend/Biatec.Chat.Api/Evidence/Recording/**"]
branch: ""
owner: ""
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
