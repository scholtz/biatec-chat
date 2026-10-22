---
id: T-120
title: "Close the cancel versus consent-redemption race (BCHAT-002)"
status: in-progress
priority: P2
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/Cancel/**", "backend/Biatec.Chat.Api/Chat/Confirm/**", "backend/Biatec.Chat.Api/Consent/**", "backend/Biatec.Chat.Tests/Chat/**", "backend/Biatec.Chat.Tests/Consent/**"]
parallel: yes
conflicts_with: []
issue: "#255"
branch: "task/T-120"
owner: "session-20261022-1800"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-120 - Close the cancel versus consent-redemption race (BCHAT-002)

## Context

Security audit finding BCHAT-002 (Medium, issue #255). `CancelService` flips `Confirmed -> Cancelled` after checking for
an `Approved` consent, while `ConfirmService.ResumeAfterConsentAsync` loads the `AwaitingConsent` step without claiming
it, signs, and `SubmitAsync` overwrites the tracked step and preview. A cancel that returns 200 in between is
overwritten and the transaction lands on chain.

## Goal

Once a cancel has succeeded, nothing is signed or submitted for that preview; once signing has been claimed, cancel is refused.

## Scope

- In: atomic claim of the step in `ResumeAfterConsentAsync` (conditioned on the preview still `Confirmed`, abort on 0
  rows); `CancelService` refuses a claimed (`Signed`) step; conditional updates for the `SubmitAsync` transitions;
  interleaving test.
- Out: BCHAT-011 (pre-sign gates on resumption).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] Interleaving test: Cancel succeeds while the sign call is blocked, release; `SubmitTransactionToBlockchainAsync` is not called and the preview stays `Cancelled`.
- [ ] A cancel after the step is claimed for signing is refused.
- [ ] Existing confirm, consent and cancel tests stay green.

## Verification

`dotnet test backend --filter "Confirm|Consent|Cancel"`, then the full suite.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-22 18:00 · claude · (new) → backlog · created from GitHub issue #255 (BCHAT-002); blockers scan: none
- 2026-10-22 18:01 · scholtz · taken, and session-20261022-1800 · backlog → in-progress · claimed
