---
id: T-083
title: "Bug: session endpoint must return the signed-in email and primary address"
status: review
priority: P2
epic: "Sessions & auth"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Sessions/SessionEndpoints.cs", "backend/Biatec.Chat.Tests/Sessions/**"]
parallel: no
conflicts_with: [T-074, T-080]
issue: "#152"
branch: "task/T-083"
owner: "worker-T-083"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-20
updated: 2026-10-20
---

# T-083 · Bug: session endpoint must return the signed-in email and primary address

## Context

Spec, User Experience 2: *"The header shows the Biatec AI chip, the current tier ("Guest", or the signed-in email with
the active primary address)"*. Personal data: *"email and addresses are stored as the OIDC sub plus the address strings
needed for evidence"* (so the email must not be persisted in PostgreSQL).

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #6).

- `Sessions/SessionEndpoints.cs:102-104` always returns `Email = null` and `PrimaryAddress = null`, with a comment
  waiting for T-009, which is done. The widget header (`ShellHeader.vue:11-33`) already renders both when present.
- Source options: the `email` claim of the ID token held encrypted in the vault (no new storage), and the primary
  address from Biatec MCP `listAlgorandAddresses` (first/primary entry) cached in the vault entry or Redis for the
  session, never in the database.
- Contracts/Session.cs already has both fields, so no protocol regeneration is expected.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-083 in biatec-chat. Read PRODUCT-DESCRIPTION.md User Experience item 2 and the personal-data rules in
Session Recording And Evidence. Read Sessions/SessionEndpoints.cs, Contracts/Session.cs, Vault/TokenVaultEntry.cs,
Auth/IdTokenValidator.cs and the widget ShellHeader.vue. Decide where email (ID token claim) and the primary address
(which MCP call, what "primary" means) come from without persisting either in PostgreSQL, and what GET /session
should do when Biatec MCP is unreachable.
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

APPROVED, 2026-10-20 (worker, recorded by the coordinator). Evidence: `Sessions/SessionEndpoints.cs:102-104` hard-coded
`Email = null, PrimaryAddress = null` (waiting on T-009, done); the widget renders both (`ShellHeader.vue:11-12,30-37`,
`BiatecChatRoot.vue:275`); the contract has both (`Contracts/Session.cs:29-31`); sources: the ID token's `email` claim
in the vault entry (`AuthEndpoints.cs:190-197`, `IdTokenValidator.cs:38`) and the first `listAlgorandAddresses`
entry. Spec `PRODUCT-DESCRIPTION.md:254` ("the signed-in email with the active primary address") and `:332` (data
minimization).

## Goal

A signed-in user sees their email and active primary address in the widget header; nothing new is written to the database.

## Scope

- In: fill `Email` and `PrimaryAddress` in the session response from the vault/ID token and a cached MCP read; tests.
- Out: widget changes (header already renders them).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-074, T-080: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [x] GET /session for a signed-in session returns the ID token's email and the primary address
- [x] Guests get nulls
- [x] No new column or table stores email or address
- [x] An MCP outage still returns the session (address null), never a 5xx

## Implementation notes

If the vault entry needs a new field, T-071 (review) owns `Vault/**`: coordinate or wait for its merge.

## Verification

`dotnet test --filter Session`; sign in on the playground and check the header.

## Hand-off

- **Summary:** GET /session fills Email (ID token `email` claim from the vault) and PrimaryAddress (first `listAlgorandAddresses` entry) for signed-in sessions through a singleton `SessionProfile`: cached per session (email + address together; 10 min, failures 30 s), filled in the background single-flight, never blocking the request, never triggering or cancelling a token refresh (only runs while the stored access token is valid beyond the refresh skew + 1 min; the MCP call gets no cancellable token). Guests get nulls; nothing is persisted; no token text in the body. `Sessions:Profile` options.
- **Branch:** `task/T-083` @ `528d0d0` (merged with main), PR #172 (`Closes #152`).
- **Files changed:** `Sessions/SessionEndpoints.cs`, `Tests/Sessions/SessionProfileTests.cs`.
- **How it was verified:** Session 89 passed; full backend on current main 1110 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed (refresh cancellation, blocking request, per-poll vault decrypt, single-flight, required parameters, injectable timeout).
- **Known limitations:** per-process cache (not Redis across pods); no invalidation when the wallet changes; the address appears on the poll after sign-in; skipped near token expiry until the chat path refreshes.
- **Follow-up tasks created:** none (proposed: return the session's scope names, `Scopes = []` today).
- **Unblocks:** no task is blocked by T-083.
- **Open questions:** is the "active primary address" the first listAlgorandAddresses entry, or per selected network?

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #6)
- 2026-10-20 03:39 · LukassSoukup · taken · status stays backlog
- 2026-10-20 03:39 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 04:12 · coordinator · in-progress → review · branch task/T-083 @ 528d0d0, PR #172; analysis verdict APPROVED recorded
