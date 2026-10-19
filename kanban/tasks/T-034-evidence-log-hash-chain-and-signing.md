---
id: T-034
title: Evidence log hash chain and signing keys
status: done
priority: P1
epic: "Evidence"
blocked_by: [T-003, T-004]
area: ["backend/Biatec.Chat.Core/Evidence/**", "backend/Biatec.Chat.Api/Evidence/Jwks/**"]
parallel: yes
conflicts_with: []
issue: "#35"
branch: "task/T-034"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#evidence-record"
created: 2026-10-19
updated: 2026-10-19
---

# T-034 · Evidence log hash chain and signing keys

## Context

payloadHash = SHA-256(JCS({salt,payload})), entryHash = SHA-256(JCS({conversationId,seq,prevHash,kind,payloadHash,anchorRef})), signature over JCS({entryHash,kid,signedAt}); rotating keys at a JWKS endpoint including retired keys; first prevHash 64 zeros.

## Goal

Append-only, verifiable evidence API other components call.

## Scope

- In: IEvidenceLog.Append(kind, payload), salt generation, chain with concurrency safety, signing key rotation (key in secret store, dev key locally), JWKS endpoint, payload deletion keeping hashes.
- Out: Wiring into features (T-035), anchoring (T-037).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Chain verifies after 100 appends
- [ ] Deleting a payload keeps the chain valid
- [ ] Signature verifies with JWKS key
- [ ] Encodings match the spec (hex lowercase, ints as strings)

## Verification

dotnet test --filter Evidence

## Hand-off

## Review

- 2026-10-19 19:18 · approve · PR #76 merged into main @ dabdbf9 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:41 · kanban-board · blocked → backlog · blockers done: T-003, T-004
- 2026-10-19 19:05 · scholtz · taken · status stays backlog
- 2026-10-19 19:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 19:18 · user · in-progress → done · PR #76 merged into main @ dabdbf9 (synced by kanban-sync)
