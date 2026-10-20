---
id: T-107
title: "Keep message payloads 5 years instead of 90 days"
status: in-progress
priority: P2
epic: "Evidence"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Retention/**", "backend/Biatec.Chat.Api/appsettings.json", "backend/Biatec.Chat.Core/Data/Entities.cs", "backend/Biatec.Chat.Tests/Retention/**", "PRODUCT-DESCRIPTION.md", "docs/architecture.md", "docs/threat-model.md", "docs/admin-api.md", "juryQnA.md"]
parallel: yes
conflicts_with: []
issue: "#227"
branch: "task/T-107"
owner: "session-20261020-retention"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-20
updated: 2026-10-20
---

# T-107 · Keep message payloads 5 years instead of 90 days

## Context

User request (2026-10-20): raise the payload retention window from 90 days to up to 5 years. Today
`Retention:Payloads` defaults to 90 days (`RetentionOptions.cs`, `appsettings.json`) and the spec (Session Recording
And Evidence) says "full transcripts 90 days" and "message payloads (and their salts) are kept 90 days".

## Goal

By default, message content, evidence payloads and salts, and preview and step payloads are kept 5 years
(1826 days), still configurable per deployment; everything else about retention and erasure is unchanged.

## Scope

- In: `RetentionOptions.Payloads` default and `appsettings.json` `Retention:Payloads` → `1826.00:00:00`; comments
  naming 90 days; the spec's two retention bullets (user-approved spec change); docs/architecture.md,
  docs/threat-model.md, docs/admin-api.md, juryQnA.md; retention tests that assume the 90-day default.
- Out: anonymous conversations (30 days), records (7 years), user erasure on request, evidence key rotation.

## Parallel work

Parallel work possible: **yes**. No other unfinished task touches retention.

## Acceptance criteria

- [ ] Default payload window is 5 years in code and appsettings, and stays below `Records` (7 years)
- [ ] Spec and docs say 5 years wherever they said 90 days for payloads/transcripts
- [ ] Retention tests pass

## Verification

`dotnet test` filtered to Retention; grep for leftover "90 days" payload statements.

## Hand-off

## Review

## Activity log

- 2026-10-20 09:21 · LukassSoukup · (new) → in-progress · user request: payload retention up to 5 years; blockers scan: none
