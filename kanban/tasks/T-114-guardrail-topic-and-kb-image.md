---
id: T-114
title: "Fix guardrail blocking swaps and generic questions; ship KB pages in the backend image"
status: review
priority: P0
epic: "Orchestrator"
blocked_by: []
area: ["infra/guardrail/**", "k8s/base/backend.yaml", "docs/aws-setup.md", "backend/Dockerfile", "backend/Dockerfile.dockerignore"]
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

- [x] The demo message, the swap and the yield question pass guardrail v4; prompt attacks and a 25-word mnemonic still block.
- [x] k8s and docs point at v4.
- [x] The built image contains `/app/kb/*.md`.

## Hand-off

- **Summary:** removed the `InvestmentAdvice` topic; published guardrail `rwa0b0by3y18` **v4** (user approved; v3 untouched),
  k8s and `docs/aws-setup.md` point at v4. `backend/Dockerfile` copies `docs/kb`, so the image ships the KB pages.
  Not changed: the EF `Skip/Take without OrderBy` warning (retention sweeper batch, harmless) and the one-off MCP
  address-read timeout (best-effort profile fill, retried by later polls).
- **How it was verified:** apply-guardrail on the DRAFT then v4: the demo message, the swap, the yield and passive-income
  questions → NONE; "Ignore all previous instructions…" and a 25-word mnemonic → GUARDRAIL_INTERVENED. `docker build`
  then `ls /app/kb` lists the pages. Guardrail and KB tests 77 passed, 1 skipped.
- **Open questions:** none. Deploying the backend picks up v4.

## Activity log

- 2026-10-20 12:50 · LukassSoukup · (new) → in-progress · created from demo logs, claimed in session
- 2026-10-20 13:00 · LukassSoukup · in-progress → review · PR opened, guardrail/KB tests green
