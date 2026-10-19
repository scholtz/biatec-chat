---
id: T-017
title: Tier policy test suite
status: backlog
priority: P1
epic: "Orchestrator"
blocked_by: [T-010, T-015]
area: ["backend/Biatec.Chat.Tests/Policy/**"]
parallel: yes
conflicts_with: []
issue: "#18"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#tier-enforcement"
created: 2026-10-19
updated: 2026-10-19
---

# T-017 · Tier policy test suite

## Context

Policy tests must assert the anonymous tier can never reach a wallet tool.

## Goal

Regression net for the most important security property.

## Scope

- In: Tests with a fake model forging wallet tool calls in anonymous tier, missing-scope signing, tool names not in registry, injected instructions in tool results.
- Out: New features.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Forged wallet call in anonymous tier is rejected and logged
- [ ] Signing without sign scope is refused
- [ ] Unknown tool is rejected
- [ ] Suite runs in CI command

## Verification

dotnet test --filter Policy

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-010: missing-scope test needs the scope check helper
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-010, T-015
