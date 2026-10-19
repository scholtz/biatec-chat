---
id: T-033
title: safe.propose, approve and execute capabilities
status: backlog
priority: P1
epic: "Safe"
blocked_by: [T-023, T-030, T-032]
area: ["backend/Biatec.Chat.Tools/Safe/Proposals/**"]
parallel: yes
conflicts_with: []
issue: "#34"
branch: ""
owner: ""
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-19
updated: 2026-10-20
---

# T-033 · safe.propose, approve and execute capabilities

## Context

Propose payment, axfer or DEX add-liquidity payload (funding as separate transfers); approve from another user's session with expectedPayloadVersion; execute once threshold met; admin changes are guided proposal sequences; propose and approve are separate signing calls.

## Goal

Demo steps 2 to 7.

## Scope

- In: Executors, payload preview with version, guided multi-step sequences, execution with step-up handling via T-022.
- Out: x402.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Opt-in proposal flow works for two users
- [ ] Liquidity proposal approved 2 of 2 and executed on testnet
- [ ] Changed payload invalidates approval
- [ ] Tests with fakes

## Verification

dotnet test --filter SafeProposals; testnet demo run

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-023: safe funding and box top-ups are separate wallet.transfer steps
- 2026-10-20 01:57 · kanban-board · blocked → backlog · blockers done: T-023, T-030, T-032
- 2026-10-20 01:58 · LukassSoukup · taken · status stays backlog
