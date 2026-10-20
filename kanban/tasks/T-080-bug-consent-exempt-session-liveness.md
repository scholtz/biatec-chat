---
id: T-080
title: "Bug: a session awaiting phone consent must stay live past the idle timeout"
status: in-progress
priority: P1
epic: "Sessions & auth"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Sessions/SessionService.cs", "backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Tests/Sessions/**"]
parallel: no
conflicts_with: [T-083]
issue: "#149"
branch: "task/T-080"
owner: "worker-T-080"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-20
updated: 2026-10-20
---

# T-080 · Bug: a session awaiting phone consent must stay live past the idle timeout

## Context

Spec, Action Preview: *"the sweeper itself skips the idle timeout for a session with an open ConsentRequest, so a
throttled tab cannot get the session swept mid-approval. The absolute lifetime always applies. A user who returns
inside the countdown is therefore never met by a swept session."* Minimal lifetime: the sweeper revokes sessions idle
past the timeout *"except a session with an open ConsentRequest, which is exempt from the idle timeout only"*.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #3).

- `Sweeper/SessionSweeper.cs:41-51` exempts sessions whose conversation has a pending, unexpired consent.
- But `SessionService.TouchAsync` (`SessionService.cs:60-68`, used by resolve and keepalive) and
  `AuthEndpoints.FindLiveAsync` (`AuthEndpoints.cs:298-305`) treat `LastActivityAt <= now - 15 min` as dead with no
  consent exemption; `RotateAsync` (`:121`) too. The sweeper's comment claims it is "the complement of
  SessionService's live test", which is not true.
- Effect: a tab throttled for >15 minutes while the phone approval is pending comes back, its cookie resolves to a new
  anonymous session, and the consent result can't be delivered to the user even though the old session still exists.
- Fix: one shared "is live" predicate (absolute expiry, and idle unless an open consent exists) used by the sweeper,
  TouchAsync, FindLiveAsync and RotateAsync.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-080 in biatec-chat. Read PRODUCT-DESCRIPTION.md "Action Preview" (keepalive and sweeper paragraph)
and "Minimal lifetime". Compare the liveness tests in backend/Biatec.Chat.Api/Sweeper/SessionSweeper.cs,
Sessions/SessionService.cs (TouchAsync, RotateAsync) and Auth/AuthEndpoints.cs (FindLiveAsync), plus any other
LastActivityAt/IdleTimeout comparisons (grep). Write a failing scenario: session idle 16 minutes with a pending
ConsentRequest, then GET /session or consent status. Propose one shared predicate.
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

Every liveness check agrees with the sweeper: an open consent exempts the session from the idle timeout, never from the absolute lifetime.

## Scope

- In: a single liveness predicate used by SessionService, AuthEndpoints and the sweeper; tests.
- Out: widget keepalive behaviour (already done in T-040/T-042).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-083: they change the same files.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] A session idle 16 minutes with a pending, unexpired ConsentRequest resolves to the same session (no new anonymous session)
- [ ] The same session past its absolute lifetime is dead even with an open consent
- [ ] Without an open consent, 15 minutes idle still ends it
- [ ] Sweeper and request-path liveness come from one shared predicate

## Implementation notes

Keep the query translatable by EF Core (the sweeper's `awaitingConsent` subquery is a good base).

## Verification

`dotnet test --filter Session`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-080`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #3)
- 2026-10-20 03:01 · LukassSoukup · taken · status stays backlog
- 2026-10-20 03:01 · coordinator · backlog → in-progress · dispatched to worker
