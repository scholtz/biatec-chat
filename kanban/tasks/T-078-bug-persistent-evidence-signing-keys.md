---
id: T-078
title: "Bug: persistent, shared evidence signing keys and JWKS"
status: in-progress
priority: P1
epic: "Evidence"
blocked_by: []
area: ["backend/Biatec.Chat.Core/Evidence/EvidenceKeys.cs", "backend/Biatec.Chat.Api/Evidence/Jwks/**", "backend/Biatec.Chat.Api/Evidence/Keys/**", "backend/Biatec.Chat.Tests/Evidence/Keys/**", "k8s/base/backend.yaml", "k8s/README.md"]
parallel: no
conflicts_with: [T-071, T-081, T-088]
issue: "#147"
branch: "task/T-078"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#session-recording-and-evidence"
created: 2026-10-20
updated: 2026-10-20
---

# T-078 · Bug: persistent, shared evidence signing keys and JWKS

## Context

Spec, Session Recording And Evidence: *"a server signature with a rotating key published at a JWKS endpoint, so a
user or auditor can verify a bundle offline ... Retired evidence-signing keys stay published at the JWKS endpoint,
marked with their retirement date, for as long as any entry they signed is retained."* Secrets handling: *"signing
keys for evidence live in Kubernetes secrets or AWS Secrets Manager"*. Success criterion 6: every session exports a
signed evidence bundle that verifies independently.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #1).

- Only `DevEvidenceKeyStore` exists (`backend/Biatec.Chat.Core/Evidence/EvidenceKeys.cs:30`), in memory.
- `k8s/base/backend.yaml:21-23` sets `Evidence__AllowEphemeralKeys: "true"` with `replicas: 2` (`:37`);
  `k8s/README.md:59-61` admits keys are lost on restart and differ between replicas.
- Impact: entries signed by pod A cannot be verified with the JWKS served by pod B; after any restart or rollout every
  earlier signature is unverifiable. The evidence export (T-036) and the demo's step 8 depend on this.
- No task covers it.
- Proposed fix: an `IEvidenceKeyStore` backed by a shared store (AWS KMS asymmetric ECC_NIST_P256 sign key per kid,
  or a key in Secrets Manager / k8s secret loaded by every replica), with a key table (kid, public JWK, createdAt,
  retiredAt) in PostgreSQL so the JWKS lists active and retired keys consistently on every pod. Remove
  `AllowEphemeralKeys` from the k8s base. AWS cost: KMS asymmetric key ~1 USD/month plus per-sign requests; say so
  to the user before creating it (AGENTS.md cost rule), tag `Project=biatec`, region `eu-central-1`.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-078 in biatec-chat. Read PRODUCT-DESCRIPTION.md "Session Recording And Evidence" and "Secrets
handling", then backend/Biatec.Chat.Core/Evidence/EvidenceKeys.cs, backend/Biatec.Chat.Api/Evidence/Jwks/**,
k8s/base/backend.yaml and k8s/README.md. Confirm whether any production IEvidenceKeyStore exists, whether two
replicas would publish different JWKS, and whether retired keys survive a restart. Compare KMS-asymmetric signing
with a secret-loaded key (cost, latency per entry, rotation, hackathon deadline) and recommend one.
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

Every replica signs with keys from one shared store, and the JWKS lists the same active and retired keys on every pod and after restarts.

## Scope

- In: a production `IEvidenceKeyStore`, persisted key metadata (active/retired with dates), JWKS served from it,
  rotation, k8s config without `AllowEphemeralKeys`, tests.
- Out: evidence export format (T-036), anchorRef encoding (T-081).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-071, T-081, T-088: they change the same files, and
running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [ ] Two app instances sharing the same database and key source serve identical JWKS and verify each other's entries
- [ ] A restart keeps every previously used kid in the JWKS, retired ones with their retirement date
- [ ] `k8s/base/backend.yaml` no longer sets `Evidence__AllowEphemeralKeys`; startup outside Development fails without a configured store
- [ ] No private key material in appsettings, the database or logs

## Implementation notes

Keep the `IEvidenceKeyStore` interface; register the new store by configuration (`Evidence:KeyStore=Kms|Secret`).
If KMS is chosen, the pod role needs `kms:Sign` and `kms:GetPublicKey` on that key (document in k8s/README.md).
T-071 (in review) also edits `k8s/**`: coordinate the merge.

## Verification

`dotnet test --filter Evidence` plus a test that builds two hosts over one database and cross-verifies; `kubectl kustomize k8s/main | grep -c AllowEphemeralKeys` prints 0.

## Hand-off

Filled in by the agent when it moves the task to `review`.

- **Summary:** what was done.
- **Files changed:** ...
- **How it was verified:** commands run and their results.
- **Follow-up tasks created:** IDs, or "none".
- **Unblocks:** output of `kanban/scripts/board.py unblocks T-078`: tasks that become ready once this is approved.
- **Open questions:** for the user, or "none".

## Review

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #1)
- 2026-10-20 03:21 · scholtz · taken · status stays backlog
- 2026-10-20 03:21 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
