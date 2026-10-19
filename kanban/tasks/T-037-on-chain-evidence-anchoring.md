---
id: T-037
title: On-chain evidence anchoring
status: in-progress
priority: P2
epic: "Evidence"
blocked_by: [T-034]
area: ["backend/Biatec.Chat.Api/Evidence/Anchoring/**"]
parallel: no
conflicts_with: [T-036]
issue: "#38"
branch: "task/T-037"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-19
updated: 2026-10-19
---

# T-037 · On-chain evidence anchoring

## Context

Every 10 min anchor heads of open or recently ended conversations in a Merkle root (leaf = SHA-256(0x00||JCS(...)), node = SHA-256(0x01||l||r), unpaired promoted) in a note of a tx from a known evidence account; notes carry root, sequence, previous anchor tx id; gaps recorded.

## Goal

Tamper evidence against the operator.

## Scope

- In: Anchor job, Merkle tree, EvidenceAnchor and leaves, anchorRef on first/resume entries, end markers, resume entries, inclusion proofs in export, verifier support.
- Out: Mainnet funding decisions (use testnet).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Merkle test vectors pass
- [ ] Anchor tx lands on testnet with expected note
- [ ] Bundle includes proofs and verifier checks them
- [ ] Gap is recorded when a tick is lost

## Verification

dotnet test --filter Anchoring; testnet run

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 19:18 · kanban-board · blocked → backlog · blockers done: T-034
- 2026-10-19 19:42 · scholtz · taken · status stays backlog
- 2026-10-19 19:42 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
