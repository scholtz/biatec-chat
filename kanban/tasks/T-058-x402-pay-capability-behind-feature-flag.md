---
id: T-058
title: x402.pay capability behind feature flag
status: done
priority: P3
epic: "Wallet tools"
blocked_by: [T-022, T-031]
area: ["backend/Biatec.Chat.Tools/X402/**", "builders/src/x402/**"]
parallel: yes
conflicts_with: []
issue: "#59"
branch: "task/T-058"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#x402-and-the-agent-to-agent-economy"
created: 2026-10-19
updated: 2026-10-20
---

# T-058 · x402.pay capability behind feature flag

## Context

Pay x402-protected resources with a USDC transfer built by algo-safe and settled via the facilitator; value is what OIDC decodes; flag off until after the hackathon.

## Goal

Groundwork for agent payments.

## Scope

- In: Sidecar x402 client endpoint, backend capability gated by tenant x402Enabled, preview of the transfer.
- Out: Seller side.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Disabled by default
- [ ] With flag on, a demo shop resource is paid on testnet
- [ ] Preview shows the decoded transfer value
- [ ] Tests

## Verification

dotnet test --filter X402

## Hand-off

## Review

- 2026-10-20 00:53 · approve · PR #118 merged into main @ 0c5558c (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022, T-031
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 00:53 · user · in-progress → done · PR #118 merged into main @ 0c5558c (synced by kanban-sync)
