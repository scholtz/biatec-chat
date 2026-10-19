---
id: T-013
title: Capability registry and tier gate
status: blocked
priority: P0
epic: "Orchestrator"
blocked_by: [T-001]
area: ["backend/Biatec.Chat.Core/Capabilities/**", "backend/Biatec.Chat.Tests/Capabilities/**"]
issue: "#14"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-013 · Capability registry and tier gate

## Context

Each capability declares minimum tier, scopes, moves-funds and valuation; anonymous tool list contains only requestSignIn and kb.search; every tool call is checked server-side against tier, scopes and registry plus JSON schema.

## Goal

Single source of truth for what the model may call.

## Scope

- In: Capability records for the initial set (x402.pay behind a flag), tool list per tier and scope, gate with JSON schema validation returning a model-facing error and a policy event.
- Out: Executors themselves.

## Acceptance criteria

- [ ] Anonymous tool list is exactly requestSignIn and kb.search
- [ ] Gate rejects a wallet tool call in anonymous tier
- [ ] Gate rejects schema-invalid arguments
- [ ] Policy event emitted on every rejection

## Verification

dotnet test --filter Capabilities

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
