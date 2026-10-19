---
id: T-016
title: Bedrock Guardrails on model and tool results
status: blocked
priority: P2
epic: "Orchestrator"
blocked_by: [T-014]
area: ["infra/guardrail/**", "backend/Biatec.Chat.Api/Model/Guardrails/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#model-layer"
created: 2026-10-19
updated: 2026-10-19
---

# T-016 · Bedrock Guardrails on model and tool results

## Context

Prompt injection defense for untrusted content; docs/AWS-AGENTCORE-GUIDE.md section 7 has the guardrail JSON and rules.

## Goal

Add a defense layer without changing tier enforcement.

## Scope

- In: Guardrail definition and create/version script (tag Project=biatec), guardrailConfig with sync streaming on Converse, ApplyGuardrail on untrusted tool results before they reach the model, config toggle.
- Out: Replacing server-side gates.

## Acceptance criteria

- [ ] Script creates guardrail and numbered version in eu-central-1
- [ ] Prompt-attack test input is blocked
- [ ] Injected instruction in a fake tool result is withheld
- [ ] Feature can be disabled by config

## Verification

AWS_PROFILE=hackathon ./infra/guardrail/create.sh; dotnet test --filter Guardrails

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
