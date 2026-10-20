---
id: T-087
title: "Bug: agent-mode gate consistency (preview tools, limit lowering, no Elevated tier)"
status: backlog
priority: P2
epic: "Orchestrator"
blocked_by: []
area: ["backend/Biatec.Chat.Core/Capabilities/**", "backend/Biatec.Chat.Api/Agent/**", "backend/Biatec.Chat.Api/Limits/LimitsConfirmService.cs", "backend/Biatec.Chat.Core/Data/Entities.cs", "backend/Biatec.Chat.Tests/Capabilities/**", "backend/Biatec.Chat.Tests/Agent/**"]
parallel: no
conflicts_with: [T-025, T-079, T-081, T-082]
issue: "#156"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#tier-enforcement"
created: 2026-10-20
updated: 2026-10-20
---

# T-087 · Bug: agent-mode gate consistency (preview tools, limit lowering, no Elevated tier)

## Context

Spec, User Experience 8: *"Analyze only: answers, explanations, balances and market information; fund-moving
capabilities are withheld from the model and refused server-side"*; *"Prepare for approval: the assistant may build
action previews"*. Tier Enforcement 5: *"In analyze or while paused, fund-moving capabilities are left out of the tool
list ... and confirming an open preview is refused"*. `limits.update`: *"lowering limits applies immediately, so a user
without a device can always reduce their own exposure"*. Session Tiers: *"exactly one of two tiers ... consent-elevated:
a per-action flag, never a session state"*.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #10, #11, #12).

1. **Preview tools in analyze/paused.** `CapabilityRegistry.Visibility` (`Core/Capabilities/CapabilityRegistry.cs:44-48`)
   hides only `IsFundMoving` tools. `safe.create`, `safe.propose` and `limits.update` are not fund-moving, so in
   Analyze only or Paused the model can still build previews that `AgentModePolicy.ConfirmRefusal` then refuses.
   UX 8 says only Prepare builds previews. Proposed: hide every preview-building capability (has a sign/limits step)
   in analyze/paused, keep reads.
2. **Lowering limits while paused/analyze.** `ConfirmService.ConfirmAsync` checks `ConfirmRefusal` before routing to
   `LimitsConfirmService`, so lowering a limit is refused while paused. Spec says lowering is always possible and
   immediate. Spec ambiguity: UX 8 says Pause *"disables Confirm on previews that are already open"*. Proposed: allow
   lowering-only limits changes in every mode (they reduce risk); needs a user decision if the analysis disagrees.
3. **`SessionTier.Elevated`** (`Core/Data/Entities.cs:6`) exists but production code never uses it; only
   `Tests/Capabilities/ToolGateTests.cs:216,314-317`. Remove it (check the enum's DB mapping and migrations first).

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-087 in biatec-chat. Read PRODUCT-DESCRIPTION.md Session Tiers, Tier Enforcement 5, User Experience 8,
and the limits.update row. Read Core/Capabilities/CapabilityRegistry.cs (Visibility, which capabilities produce
previews), Api/Agent/AgentModeEndpoints.cs (ConfirmRefusal), Api/Chat/Confirm/ConfirmService.cs (order of checks) and
Api/Limits/**. For each of the three findings decide APPROVED or REJECTED separately; for finding 2 quote both spec
sentences and ask the user if they conflict. For finding 3 check how SessionTier is persisted (int vs string) and
whether any row could hold Elevated.
>
> Report: (a) does the conflict still exist on current `main`, with file:line evidence; (b) is the spec text
> unambiguous, quoting it; (c) the smallest change that satisfies it and what it touches outside this task's `area`;
> (d) risks and tests needed. End with a verdict line: `APPROVED`, `PARTIAL: <approved items>` or
> `REJECTED: <reason>`, and write it into the task's Analysis verdict section.

## Analysis gate

This task starts with an analysis step. **No code may be written until the Analysis verdict below says `APPROVED`
or `PARTIAL`.**

1. Run the **Analysis prompt** above against the current `main` (the finding was recorded on `main @ 6b701e5`;
   later merges may already have fixed or changed it).
2. Write the result into **Analysis verdict**: `APPROVED` (implement as scoped), `PARTIAL` (list exactly which
   findings or acceptance criteria are approved; only those may be implemented, the rest are struck through with a
   reason), or `REJECTED` (no conflict with the spec after all, or already fixed; give the evidence).
3. `APPROVED` / `PARTIAL`: implement only the approved part, then hand off as usual.
   `REJECTED`: change no code; move the task to `review` with the evidence in the Hand-off, so the user can close it.
4. If the analysis shows the fix needs a spec decision (the spec is ambiguous or two spec sections disagree), record
   the question under Hand-off → Open questions and ask the user before implementing that part.

## Analysis verdict

_pending: filled in by the agent that runs the Analysis prompt (verdict, date, evidence, approved items)._

## Goal

Analyze only and Paused withhold every preview-building tool, lowering limits always works, and the code models exactly two tiers.

## Scope

- In: registry visibility rule, confirm-path ordering for lowering limits, removal of `SessionTier.Elevated`, tests.
- Out: the soft cap (T-025), widget copy.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-025, T-079, T-081, T-082: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] In analyze or paused, the Bedrock tool list contains no capability that builds a preview (test per capability)
- [ ] Lowering a limit is confirmable while paused and in analyze (if approved); raising is still refused
- [ ] `SessionTier` has only Anonymous and Authenticated; tests updated
- [ ] Tier policy suite stays green

## Implementation notes

A `BuildsPreview` flag on Capability is clearer than a name list. ConfirmService.cs is also touched by T-079 and T-025.

## Verification

`dotnet test --filter "ToolGate|AgentMode|TierPolicy|Limits"`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-087`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #10, #11, #12)
