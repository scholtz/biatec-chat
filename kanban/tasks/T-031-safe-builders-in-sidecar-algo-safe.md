---
id: T-031
title: Safe builders in sidecar (algo-safe)
status: backlog
priority: P1
epic: "Safe"
blocked_by: [T-027]
area: ["builders/src/safe/**"]
parallel: yes
conflicts_with: []
issue: "#32"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-19
updated: 2026-10-19
---

# T-031 · Safe builders in sidecar (algo-safe)

## Context

algo-safe: create safe, signer groups via governed proposals, proposals with typed payload (payment, axfer, app call group), approveProposal with expectedPayloadVersion, executeProposal, read helpers.

## Goal

All safe building and reading behind the sidecar.

## Scope

- In: Endpoints: safe/create, safe/list (by member), safe/proposals, proposal/create, proposal/approve, proposal/execute; return unsigned groups and decoded payload previews.
- Out: Backend capabilities.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Create safe group simulates on testnet
- [ ] Proposal with payment payload round-trips through read helper
- [ ] approve includes expectedPayloadVersion
- [ ] Tests with fixtures

## Verification

pnpm --filter builders test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 20:10 · kanban-board · blocked → backlog · blockers done: T-027
