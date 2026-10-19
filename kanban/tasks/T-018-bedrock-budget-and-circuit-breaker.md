---
id: T-018
title: Bedrock budget and circuit breaker
status: blocked
priority: P2
epic: "Orchestrator"
blocked_by: [T-014]
area: ["backend/Biatec.Chat.Api/Budget/**"]
branch: ""
owner: ""
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

## Acceptance criteria

- [ ] Cost accrues from usage per tenant
- [ ] Exhausted anonymous budget returns cached/refusal reply without a Bedrock call
- [ ] Authenticated sessions still served within reserve
- [ ] Tests with fake usage

## Verification

dotnet test --filter Budget

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
