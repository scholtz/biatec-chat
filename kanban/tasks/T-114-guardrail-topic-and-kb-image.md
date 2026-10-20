---
id: T-114
title: "Fix guardrail blocking swaps and generic questions; ship KB pages in the backend image"
status: in-progress
priority: P0
epic: "Orchestrator"
blocked_by: []
area: ["infra/guardrail/**", "k8s/base/backend.yaml", "docs/aws-setup.md", "backend/Dockerfile"]
parallel: yes
conflicts_with: []
issue: "#241"
branch: "task/T-114"
owner: "session-20261020-1250"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-114 · Fix guardrail blocking swaps and generic questions; ship KB pages in the backend image

## Context

Demo (2026-10-20, stage): "so what can you help me with, i want to make alot of money" → "I can't help with that
request." Backend log: `Bedrock guardrail blocked eu.anthropic.claude-opus-4-5-20251101-v1:0: input:topic:InvestmentAdvice`.
apply-guardrail on v3 also blocks "swap 10 ALGO to USDC", "how can I earn yield on my ALGO" and "I want passive
income from crypto". Both pods also log `Kb: serving 0 pages (0 uploaded)`: `backend/Dockerfile` never copies
`docs/kb`, so the csproj `..\..\docs\kb\**\*.md` content glob matches nothing in the image.

## Goal

Ordinary requests and core actions reach the model; buy/sell/hold advice is still refused (persona rule, spec
line 21). The image ships the local KB pages.

## Scope

- In: remove the `InvestmentAdvice` topic from the guardrail, publish v4 (user approved), point k8s and docs at v4;
  copy `docs/kb` in the backend Dockerfile.
- Out: other filters, tool-result guard code.

## Acceptance criteria

- [ ] The demo message, the swap and the yield question pass guardrail v4; prompt attacks and a 25-word mnemonic still block.
- [ ] k8s and docs point at v4.
- [ ] The built image contains `/app/kb/*.md`.

## Activity log

- 2026-10-20 12:50 · LukassSoukup · (new) → in-progress · created from demo logs, claimed in session
