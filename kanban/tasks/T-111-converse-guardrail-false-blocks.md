---
id: T-111
title: "Fix Converse guardrail blocking every answer (InvestmentAdvice topic, whole-history screening)"
status: done
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

- [x] The three demo messages get real answers in a local run against the real guardrail (Haiku).
- [x] "Should I buy ALGO now?" and "Which token will go up next week?" are still refused.
- [x] A Converse request carries exactly the latest user text as `guardContent`; host context and tool results don't.
- [x] A blocked Converse call logs the blocking policy and topic.
- [x] Config and k8s point at the new guardrail version.

## Verification

`dotnet test backend/Biatec.Chat.slnx`; local replay of the demo messages against the backend (`Bedrock__Guardrail__*`
set, Haiku) with the logs; apply-guardrail checks of the advice examples.

## Hand-off

- **Summary:** every block was the `InvestmentAdvice` deny topic (guardrail trace, local backend on `main` with
  Haiku). Converse screened the whole history on every call, so one matching answer poisoned every later turn, and
  on output the topic flagged ordinary explanations and even capability lists, not reproducibly. Fixes:
  - `BedrockModelClient` makes the turn's user message (user text and host page context) the only `guardContent`.
    Earlier turns were screened when sent, and tool results by the tool-result guard. Guardrail trace on; a block logs
    `Bedrock guardrail blocked <model>: input:topic:InvestmentAdvice` (also when the metadata has no usage).
  - Guardrail `rwa0b0by3y18` **v3** (published with the user's approval; v1 untouched): `InvestmentAdvice` is limited to
    personalised buy/sell/hold advice and price predictions, and screens **input only**. Output stays advice-free
    through a persona rule. Prompt-attack, harm and recovery-phrase filters still screen output. `k8s/base/backend.yaml`
    and `docs/aws-setup.md` point at v3; troubleshooting row added.
  - Known trade-offs (from review): advice on output is now prevented by the system prompt, not the guardrail
    (review finding kept on purpose: the output topic blocked legitimate answers). The history cache prefix misses once per turn, because the
    previous turn's message changes from guardContent to text. The system-prompt cache and in-turn tool-loop cache still hit.
- **Files changed:** `backend/Biatec.Chat.Api/Model/BedrockModelClient.cs`, `backend/Biatec.Chat.Api/Orchestration/PromptBuilder.cs`
  (one persona rule, outside the area), `infra/guardrail/guardrail.json`, `k8s/base/backend.yaml`, `docs/aws-setup.md`,
  `backend/Biatec.Chat.Tests/Guardrails/GuardrailsTests.cs`.
- **How it was verified:** `dotnet test backend/Biatec.Chat.slnx` 1325 passed, 1 skipped. Local backend with guardrail
  v3 and Haiku, demo messages replayed three times: the DEX explanation (3 KB searches), "what is my capital"
  (sign-in), "what can you help me with" and "how do fees work" all answered. "Should I buy ALGO now?", "Which token
  will go up next week?" and "Is it better to put my USDC in the EURS/USDC pool or keep it?" refused (input topic).
  Apply-guardrail checks on the DRAFT before each publish. Reviews: medium (9 findings: 6 fixed, 2 trade-offs
  noted, 1 accepted), then low (none). Branch `task/T-111` @ 1e3b0a2, PR #236, CI green.
- **Follow-up tasks created:** none.
- **Unblocks:** nothing.
- **Open questions:** none. Deploying the backend picks up v3 from `k8s/base/backend.yaml`.

## Review

- 2026-10-20 11:24 · approve · PR #236 merged into main @ 58be230 (recorded by kanban-sync)

## Activity log

- 2026-10-20 11:02 · coordinator · (new) → backlog · created from demo testing (guardrail blocks every answer)
- 2026-10-20 11:02 · LukassSoukup · taken · status stays backlog
- 2026-10-20 11:02 · session-20261020-1102 · backlog → in-progress · claimed in session
- 2026-10-20 11:23 · session-20261020-1102 · in-progress → review · PR #236, full suite green, CI green, review clean
- 2026-10-20 11:24 · user · review → done · PR #236 merged into main @ 58be230 (synced by kanban-sync)
