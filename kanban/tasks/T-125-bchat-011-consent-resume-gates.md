---
id: T-125
title: "Re-run the agent-mode and x402 gates when an approved consent resumes (BCHAT-011)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/Confirm/**", "backend/Biatec.Chat.Tests/Signing/**"]
parallel: yes
conflicts_with: []
issue: "#264"
branch: "task/T-125"
owner: "session-20261022-2300"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-125 - Re-run the agent-mode and x402 gates when an approved consent resumes (BCHAT-011)

## Context

Security audit finding BCHAT-011 (Low, issue #264). `ConfirmAsync` refuses a paused or analyze-only session and an
x402 payment whose tenant flag or settlement handler is gone, before signing. `ResumeAfterConsentAsync`, which signs
after the phone approval, runs neither: pausing the agent or turning x402 off during the phone wait does not stop the
action. The no-device gate does not apply (an approval proves an active device).

## Goal

An approval that arrives after the session was paused (or set to analyze only), or after x402 was disabled, signs nothing
and ends the preview.

## Scope

- In: the agent-mode refusal and the x402 flag/handler refusal at the top of `ResumeAfterConsentAsync`, sharing the x402 check with `SignAndSubmitAsync`; tests.
- Out: BCHAT-002 (already merged).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Confirm, StepUp, pause the session, approve: resume returns `AgentRestricted`, nothing is signed or submitted, the step and preview end cancelled.
- [x] Same for analyze-only mode.
- [x] x402 preview whose tenant flag is turned off before the approval: resume fails with `feature_disabled`, nothing signed.
- [x] Existing confirm, consent and cancel tests stay green.

## Verification

`dotnet test backend --filter "Confirm|Consent|Cancel"`, then the full suite.

## Hand-off

- **Summary:** `ResumeAfterConsentAsync` now refuses, before claiming the step for signing: a paused or analyze-only session (step and preview cancelled, `AgentRestricted`), an x402 payment whose tenant flag or settlement handler is gone, and a preview a freshness gate calls stale. The x402/stale refusals claim the step row atomically first, so a concurrent cancel is never overwritten. PR #307.
- **Files changed:** Api/Chat/Confirm/ConfirmService.cs, Tests/Signing/ConfirmServiceTests.cs.
- **How it was verified:** dotnet test (full suite) passed; tests for paused, analyze, x402 disabled, stale, and cancel-then-refusal.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The Sign-scope check and the cap re-check are still not repeated on resume (the elevated token carries the scope; the cap was reserved at confirm). The consent poll still reports `Approved` next to a refusing result; the widget should key on the result outcome. Want follow-ups?

## Review

- 2026-10-22 20:12 · approve · PR #307 merged into main @ aa8eab6 (recorded by kanban-sync)

## Activity log

- 2026-10-22 23:00 · claude · (new) → backlog · created from GitHub issue #264 (BCHAT-011); blockers scan: none
- 2026-10-22 23:01 · scholtz · taken, and session-20261022-2300 · backlog → in-progress · claimed
- 2026-10-22 20:12 · user · in-progress → done · PR #307 merged into main @ aa8eab6 (synced by kanban-sync)
