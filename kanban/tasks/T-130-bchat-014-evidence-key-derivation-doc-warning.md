---
id: T-130
title: "Warn about and document the single-master derivation of evidence signing keys (BCHAT-014)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Evidence/Jwks/EvidenceJwks.cs", "backend/Biatec.Chat.Tests/Evidence/Keys/**", "docs/threat-model.md"]
parallel: yes
conflicts_with: []
issue: "#267"
branch: "task/T-130"
owner: "session-20261023-1030"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-130 - Warn about and document the single-master derivation of evidence signing keys (BCHAT-014)

## Context

Security audit finding BCHAT-014 (Low, issue #267). With `Evidence:KeyStore=Secret` every evidence signing key is derived with HKDF from
one master secret and the kid, so rotating the key gives no recovery from a master compromise: past and future signatures can be forged.
The finding's fix is to prefer `Kms` for production or independent per-kid secrets, and to document it in the threat model.

## Goal

An operator who chooses the Secret evidence key store is told what it does and does not protect, in the logs and in the threat model.

## Scope

- In: a start-up warning when the Secret key store is built; a threat-model section; a test for the warning.
- Out: implementing independent per-kid secrets (a design change; a follow-up if wanted) and switching the deployment to Kms (needs AWS resources).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Building the Secret evidence key store logs a warning that names the single-master derivation and recommends Kms.
- [x] `docs/threat-model.md` describes the limit and the recommendation.
- [x] Existing evidence tests stay green.

## Verification

`dotnet test backend --filter Evidence`.

## Hand-off

- **Summary:** Building the Secret evidence key store now logs a warning that every key derives from one master (rotation gives no recovery from its compromise) and recommends Kms; docs/threat-model.md has a section on it. No behaviour change otherwise.
- **Files changed:** Api/Evidence/Jwks/EvidenceJwks.cs, docs/threat-model.md, Tests/Evidence/Keys/EvidenceKeyStoreTests.cs.
- **How it was verified:** dotnet test full suite; new test captures the warning from the real AddEvidence registration.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The finding's actual fix (Kms, or independent per-kid secrets) is not done: Kms needs AWS keys, and per-kid secrets are a design change to key storage. This task only warns and documents. Want a follow-up for independent per-kid secrets?

## Review

- 2026-10-23 10:40 · approve · PR #313 merged into main @ 7ba13e7 (recorded by kanban-sync)

## Activity log

- 2026-10-23 12:00 · claude · (new) → backlog · created from GitHub issue #267 (BCHAT-014); blockers scan: none
- 2026-10-23 10:30 · scholtz · taken, and session-20261023-1030 · backlog → in-progress · claimed
- 2026-10-23 10:40 · user · in-progress → done · PR #313 merged into main @ 7ba13e7 (synced by kanban-sync)
