---
id: T-081
title: "Bug: anchorRef in the entry hash must be the anchor transaction ID"
status: in-progress
priority: P2
epic: "Evidence"
blocked_by: []
area: ["backend/Biatec.Chat.Core/Evidence/**", "backend/Biatec.Chat.Core/Data/Entities.cs", "backend/Biatec.Chat.Api/Evidence/Anchoring/**", "backend/Biatec.Chat.Api/Migrations/**", "backend/Biatec.Chat.Tests/Evidence/**"]
parallel: no
conflicts_with: [T-036, T-078, T-087, T-088]
issue: "#150"
branch: "task/T-081"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#evidence-record"
created: 2026-10-20
updated: 2026-10-20
---

# T-081 · Bug: anchorRef in the entry hash must be the anchor transaction ID

## Context

Spec, Evidence Record: *"entryHash = SHA-256(JCS({conversationId, seq, prevHash, kind, payloadHash, anchorRef})),
where anchorRef is the transaction ID of the latest confirmed anchor on a conversation's first entry and on the first
entry after a resume, and null otherwise ... Encodings are fixed so two implementations agree byte for byte."*

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #4).

- `EvidenceEntry.AnchorRef` is `Guid?` (`Core/Data/Entities.cs:111`); `EvidenceLog.cs:66` hashes
  `anchorRef?.ToString("D")`; `AnchorService.cs:320` derives that GUID as the first 16 bytes of SHA-256(txId).
- An independent verifier written from the spec puts the Algorand transaction ID in `anchorRef` and computes a
  different `entryHash` for every first/resume entry, so bundles fail the spec's own verification rule.
- Fix: store and hash the transaction ID string; migration converting existing values is impossible (one-way hash),
  so either re-key from `EvidenceAnchors.AnchorTxId` via the derived id (the mapping is computable forwards: compute
  the GUID of each known txId and join) or accept that existing dev/stage chains are reset. Entry hashes of already
  written entries cannot change (the chain would break), so existing rows keep a legacy marker or are discarded.
- T-036 (evidence export and offline verifier, in progress) consumes this encoding: coordinate with its owner.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-081 in biatec-chat. Read the Evidence Record section of PRODUCT-DESCRIPTION.md and
packages/consent-protocol / docs for any published evidence encoding. Inspect Core/Evidence/EvidenceHasher.cs,
EvidenceLog.cs, EvidenceVerifier.cs, Api/Evidence/Anchoring/AnchoredEvidenceWriter.cs, AnchorVerifier.cs and
AnchorService.cs (AnchorIds). Check what T-036's branch (origin/task/T-036) expects for anchorRef. Decide how
existing rows are handled (stage/main databases: are there real chains to keep?) and propose the migration.
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

`anchorRef` is the anchor's Algorand transaction ID everywhere it is stored, hashed, exported and verified.

## Scope

- In: entity/column type, hashing, writer, verifiers, migration, test vectors.
- Out: export bundle layout (T-036) beyond the field's value.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036, T-078, T-087, T-088: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] A first entry's `entryHash` equals SHA-256(JCS({..., anchorRef: <txId>})) computed by an independent test helper
- [ ] AnchorVerifier resolves anchorRef directly as a transaction ID
- [ ] The migration's handling of existing rows is documented in the Hand-off

## Implementation notes

Do not rewrite hashes of existing entries. Coordinate with T-036's owner before merging.

## Verification

`dotnet test --filter Evidence`.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-081`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #4)
- 2026-10-20 04:05 · scholtz · taken · status stays backlog
- 2026-10-20 04:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
