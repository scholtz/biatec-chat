---
id: T-088
title: "Bug: evidence anchoring with two replicas and in the k8s deployment"
status: done
priority: P2
epic: "Evidence"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Evidence/Anchoring/AnchoringRegistration.cs", "backend/Biatec.Chat.Tests/Evidence/Anchoring/**", "k8s/base/backend.yaml", "k8s/README.md"]
parallel: no
conflicts_with: [T-071, T-081, T-078]
issue: "#157"
branch: "task/T-088"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-20
updated: 2026-10-20
---

# T-088 · Bug: evidence anchoring with two replicas and in the k8s deployment

## Context

Spec, Threat Model: *"every 10 minutes the backend anchors the current head hash of every conversation ... batched
into one Merkle root, in the note field of an Algorand transaction from a dedicated, publicly known evidence account.
Each note carries the root, an anchor sequence number, and the previous anchor's transaction ID"*. Availability: the
backend is horizontally scaled.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #13).

- Unverified in the audit, to be confirmed here:
  - `AnchoringRegistration.cs:147-177` starts a `PeriodicTimer` tick on every pod; `AnchorService.TickAsync` has no
    cross-pod lock or leader election. With `replicas: 2` two pods may build the same sequence number at once and both
    broadcast a transaction before the database rejects one, putting two notes with the same sequence on chain.
  - The k8s manifests never set `Evidence__Anchoring__Enabled` or the evidence account mnemonic secret, so anchoring
    does not run in stage/main at all, and the operator-tamper guarantee is absent there.
- Proposed: a PostgreSQL advisory lock (or Redis lock) around the whole tick, held across broadcast; deploy config with
  the mnemonic from a k8s secret (testnet account funded from the dispenser), documented in k8s/README.md.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-088 in biatec-chat. Read the anchoring paragraph of the Threat Model Highlights in
PRODUCT-DESCRIPTION.md. Read backend/Biatec.Chat.Api/Evidence/Anchoring/** fully, especially when an
EvidenceAnchorAttempts row is inserted relative to the broadcast, and decide whether two concurrent ticks can both
broadcast. Write a test with two AnchorService instances over one database if feasible. Check k8s/** and infra/** for
anchoring configuration and the evidence account secret.
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

Exactly one anchor transaction per sequence number is broadcast however many replicas run, and anchoring is enabled in the deployment.

## Scope

- In: cross-replica serialisation of the tick, k8s config and secret reference, docs, tests.
- Out: anchorRef encoding (T-081), signing keys (T-078).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-071, T-081, T-078: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] Two concurrent ticks over one database broadcast at most one transaction (test)
- [ ] k8s stage/main enable anchoring with the mnemonic from a secret, never in the manifest
- [ ] k8s/README.md documents the evidence account and how to fund it

## Implementation notes

T-071 (review) also edits `k8s/**`.

## Verification

`dotnet test --filter Anchoring`; `kubectl kustomize k8s/stage | grep Anchoring`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-088`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

- 2026-10-20 06:18 · approve · PR #182 merged into main @ 82a3880 (recorded by kanban-sync)

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #13)
- 2026-10-20 04:35 · scholtz · taken · status stays backlog
- 2026-10-20 04:35 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 06:18 · user · in-progress → done · PR #182 merged into main @ 82a3880 (synced by kanban-sync)
