---
id: T-110
title: "Safe executors: move instruction text out of guardrail-screened results"
status: done
priority: P2
epic: "Safe"
blocked_by: [T-109]
area: ["backend/Biatec.Chat.Api/Safe/**", "backend/Biatec.Chat.Tests/Safe/**"]
parallel: yes
conflicts_with: []
issue: "#232"
branch: "task/T-110"
owner: "session-20261020-1048"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-110 · Safe executors: move instruction text out of guardrail-screened results

## Context

Follow-up from T-109. During the 2026-10-20 demo the tool-result guardrail withheld legitimate results: the
executor's own instruction text, sent inside the screened JSON, tripped `PROMPT_ATTACK` (LOW confidence, the
filter runs at HIGH strength; verified with the deployed guardrail `rwa0b0by3y18` v1). T-109 added
`ToolExecutionResult.Guidance` (the executor's fixed instruction, sent after the untrusted wrapper and never screened)
and moved the liquidity listing's instruction there.

Three results still carry an `"instruction"` field inside the screened content:

- `Safe/Create/SafeCreateExecutors.cs` ≈ line 114 ("Nothing has been built. Ask the user for …") and ≈ line 337
  (`safe.list`: "Show each safe's name, app id and address, …").
- `Safe/Proposals/SafeProposalExecutors.cs` ≈ line 56 (`outcome.Message + …`, which includes the safe's
  add-liquidity pool listing from `SafeProposalService`).

## Goal

Safe tool results that guide the model aren't withheld because of their own guidance.

## Scope

- In: move each fixed instruction into `Guidance`. Where the text mixes in upstream data (`outcome.Message` may
  carry builder or chain text), keep that part in the screened content and only the fixed wording in `Guidance`.
  Update the Safe tests that read `instruction`.
- Out: other executors' error strings ("Tell the user …" in error results); they passed the guardrail in T-109's
  checks.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes the Safe executors.

## Acceptance criteria

- [x] No `"instruction"` field is left in screened tool content (`grep -rn '"instruction"' backend/Biatec.Chat.Api`).
- [x] Guidance never contains upstream data (reviewed per call site).
- [x] Safe tests pass; a test per executor asserts the guidance is in `ToolExecutionResult.Guidance`.

## Verification

`dotnet test backend/Biatec.Chat.slnx --filter FullyQualifiedName~Safe`. Optional live check: `aws bedrock-runtime
apply-guardrail` (eu-central-1, guardrail `rwa0b0by3y18` v1) on a recorded `safe.list` result before/after.

## Hand-off

- **Summary:** safe.create (missing details), safe.list and the safe proposal executors send their fixed wording as `ToolExecutionResult.Guidance`; service messages stay screened as `"message"`. task/T-110 @ 69200d2, merged in PR #234.
- **Files changed:** `Safe/Create/SafeCreateExecutors.cs`, `Safe/Proposals/SafeProposalExecutors.cs`, `Tests/Safe/Create/SafeCreateTests.cs`, `Tests/Safe/Proposals/SafeProposalFlowTests.cs`.
- **How it was verified:** full backend suite 1321 passed / 1 skipped; no `"instruction"` left in `backend/Biatec.Chat.Api`; live apply-guardrail on a recorded safe.list result: NONE before and after (sample did not reproduce the block).
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** SafeProposalService messages (Biatec.Chat.Tools) still phrase steps as instructions inside the screened data; split them only if the guardrail is seen withholding them.

## Review

- 2026-10-20 10:55 · approve · PR #234 merged into main @ 4b2282d (recorded by kanban-sync)

## Activity log

- 2026-10-20 10:28 · coordinator · (new) → blocked · follow-up from T-109 (instruction text trips the tool-result guardrail)
- 2026-10-20 10:43 · kanban-board · blocked → backlog · blockers done: T-109
- 2026-10-20 10:48 · session-20261020-1048 · backlog → in-progress · taken by LukassSoukup, claimed (T-109 merged in PR #233)
- 2026-10-20 10:55 · user · in-progress → done · PR #234 merged into main @ 4b2282d (synced by kanban-sync)
