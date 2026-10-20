---
id: T-111
title: "Fix Converse guardrail blocking every answer (InvestmentAdvice topic, whole-history screening)"
status: in-progress
priority: P0
epic: "Orchestrator"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Model/BedrockModelClient.cs", "infra/guardrail/**", "k8s/base/backend.yaml", "backend/Biatec.Chat.Tests/Guardrails/**", "docs/aws-setup.md"]
parallel: yes
conflicts_with: []
issue: "#235"
branch: "task/T-111"
owner: "session-20261020-1102"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-111 · Fix Converse guardrail blocking every answer (InvestmentAdvice topic, whole-history screening)

## Context

Demo testing (2026-10-20): "Explain how the Biatec DEX and its concentrated liquidity pools work." → four KB searches,
then "I can't share that response." Every later message ("ok what is my capital", "what can you help me with") →
"I can't help with that request." These are the guardrail's `blockedOutputsMessaging` / `blockedInputMessaging`
from `infra/guardrail/guardrail.json`, returned by the Converse call's `guardrailConfig`
(`Model/BedrockModelClient.cs`).

Reproduced locally (backend from `main`, guardrail `rwa0b0by3y18` v1, Haiku) with guardrail trace enabled:
1. Turn 1: **output** blocked by topic `InvestmentAdvice` (an educational explanation of pools and fees).
2. Turns 2–3: **input** blocked by topic `InvestmentAdvice`. Without `guardContent` blocks Converse screens the
   whole conversation (all earlier turns and tool results) on every call, so once the history contains DEX content
   every later turn is blocked: the conversation is poisoned.

The KB page `docs/kb/algorand-basics.md` on its own also matches the topic as input.

## Goal

Relevant questions get proper answers through Bedrock with the full tool set, while personalised buy/sell/hold advice
and price predictions are still refused.

## Scope

- In: mark only the current user message as `guardContent` in Converse (history was screened when it was sent; tool
  results are screened by the tool-result guard). Narrow the `InvestmentAdvice` topic definition so explaining how
  products, pools, fees and liquidity work is not advice; publish a new guardrail version and point config at it.
  Log the guardrail trace (blocking policy and topic) when Converse is blocked.
- Out: the tool-result guard (T-109, T-110).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes the model client or the guardrail.

## Acceptance criteria

- [ ] The three demo messages get real answers in a local run against the real guardrail (Haiku).
- [ ] "Should I buy ALGO now?" and "Which token will go up next week?" are still refused.
- [ ] A Converse request carries exactly the latest user text as `guardContent`; host context and tool results don't.
- [ ] A blocked Converse call logs the blocking policy and topic.
- [ ] Config and k8s point at the new guardrail version.

## Verification

`dotnet test backend/Biatec.Chat.slnx`; local replay of the demo messages against the backend (`Bedrock__Guardrail__*`
set, Haiku) with the logs; apply-guardrail checks of the advice examples.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-20 11:02 · coordinator · (new) → backlog · created from demo testing (guardrail blocks every answer)
- 2026-10-20 11:02 · LukassSoukup · taken · status stays backlog
- 2026-10-20 11:02 · session-20261020-1102 · backlog → in-progress · claimed in session
