---
id: T-109
title: "Fix add-liquidity flow: pool listing withheld by the guardrail"
status: backlog
priority: P1
epic: "DEX"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Dex/Liquidity/**", "backend/Biatec.Chat.Api/Model/Guardrails/**", "backend/Biatec.Chat.Api/Orchestration/ConversationOrchestrator.cs", "backend/Biatec.Chat.Tests/**/Liquidity*", "backend/Biatec.Chat.Tests/**/Guardrail*"]
parallel: yes
conflicts_with: []
issue: "#230"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-109 · Fix add-liquidity flow: pool listing withheld by the guardrail

## Context

Found in demo testing (2026-10-20):

> **User:** Add liquidity to the Biatec DEX EURS/USDC pool.
> **AI:** asks how much EURS and USDC, and for the pool app id.
> **User:** 10 EURS
> **AI:** tool status "Preparing the liquidity deposit — Failed", then: "Sorry, I'm unable to retrieve the
> available EURS/USDC pools at the moment — the result couldn't be used."

"The result couldn't be used" is the model paraphrasing `GuardrailMessages.ToolResultWithheld`
(`Model/Guardrails/ToolResultGuard.cs`). So `dex.addLiquidity` ran (status label from
`DexAddLiquidityExecutor.StatusLabel`), and its result was replaced in `ConversationOrchestrator` (≈ line 198)
because `BedrockToolResultGuard.ScreenAsync` returned `Withheld`. The **Failed** status fits: a pool listing is a
success result, and only a withhold (or an executor error) turns it into an error. `ScreenAsync` withholds in two
cases:

1. **Guardrail intervened** (backend logs `Guardrail withheld a result of tool.dex.addLiquidity (<n> chars)`).
2. **ApplyGuardrail call failed, fail closed** (backend logs `Guardrail check for tool.dex.addLiquidity failed
   (<ErrorType>)`). Only the exception type is logged, not its message.

Already checked on 2026-10-20 (hackathon account, guardrail `rwa0b0by3y18` v1, the one `k8s/base/backend.yaml`
configures):
- `apply-guardrail` with a sample `DescribePools` output **including** its `instruction` text → `NONE`. Same for
  the empty-listing instruction, the "No pool was named…" refusal and the builder/tool error texts → all `NONE`.
  So our own guidance text doesn't trip `PROMPT_ATTACK` (first suspicion, ruled out for these samples).
- IAM role `biatec-chat-bedrock-role` allows `bedrock:ApplyGuardrail` on `guardrail/*` in eu-central-1, and the
  same role's `ConverseStream` calls succeeded during the demo (last at 09:43 CEST).
- CloudTrail event history doesn't show `ApplyGuardrail` calls at all (not even direct CLI calls), so it can't tell
  whether the pod's call reached AWS.

Remaining suspects: (a) the ApplyGuardrail call throws in the deployed pod (credentials/client config through Roles
Anywhere, request validation such as an empty or oversized block, or a timeout); (b) the real pool listing (real
pool data from the builders sidecar) trips a filter that the sample didn't. The pod logs from the demo
(`kubectl logs` on the chat backend around 09:40 CEST, 2026-10-20) decide between them; this machine had no cluster
context when the task was written.

Secondary issues from the same exchange:
- The user gave only "10 EURS"; the model still called the tool (probably with an invented or zero `amountB`).
  The spec's Biatec CLAMM deposit takes both assets, so the model should ask for the USDC amount, or the executor
  should explain how the second amount follows from the pool price.
- The model's reply blamed "retrieving pools" for a guardrail withhold; the withheld message should let it say
  something accurate (a safety check stopped the result; try again / contact support).

## Goal

"Add liquidity to the EURS/USDC pool" in the demo leads to a pool choice and a deposit preview, legitimate tool results
aren't withheld, injection screening of untrusted (upstream) data stays in place, and the next withhold is
diagnosable from the logs.

## Scope

- In: confirm the cause from the pod logs (or reproduce locally with the builders sidecar and the real guardrail,
  logging the exact `ApplyGuardrail` exception and assessment). Log enough to diagnose next time: the exception
  message (it carries no user data) on a failed check, and the intervening filter types and confidence on a
  withhold. Fix the confirmed cause. If it's (a), fix the call or config; if it's (b), keep the backend's own
  guidance and its own numeric fields out of the screened text so only upstream strings are screened. Make the add-liquidity
  path ask for the missing second amount (or derive it from the pool price with a clear preview). Improve the
  withheld message so the model doesn't misreport the cause.
- Out: changing the guardrail's filter strengths (only if confirmed necessary, and documented); swap flow;
  widget changes (T-108).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes the liquidity executors, the guardrail code or the
orchestrator.

## Acceptance criteria

- [ ] The cause is recorded in the Hand-off with evidence (log line or ApplyGuardrail assessment output).
- [ ] A failed guardrail check logs the exception type and message; a withhold logs the filter types; tests cover
      both log paths.
- [ ] A test shows a `dex.addLiquidity` call with `poolAppId 0` and asset ids returns the pool list to the model
      (status Succeeded), and injected text inside upstream pool data is still withheld.
- [ ] With only one amount given, the flow asks for the other amount (or previews the derived one); it never builds
      with an invented amount.
- [ ] The withheld message lets the model say a safety check blocked the result, not that data couldn't be fetched.
- [ ] Manual check against the stage/demo backend: "Add liquidity to the Biatec DEX EURS/USDC pool" → pool choice →
      amounts → deposit preview. Use Haiku for live smoke tests.

## Verification

`dotnet test backend` (liquidity and guardrail tests). Live: one ApplyGuardrail call with the deployed guardrail
on the before/after result text (`aws bedrock-runtime apply-guardrail --region eu-central-1 …`), then the chat flow
above with logs showing no `Guardrail withheld` line.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-20 12:00 · coordinator · (new) → backlog · created from demo testing (add-liquidity result withheld)
