---
id: T-058
title: x402.pay capability behind feature flag
status: blocked
priority: P3
epic: "Wallet tools"
blocked_by: [T-022, T-031]
area: ["backend/Biatec.Chat.Tools/X402/**", "builders/src/x402/**"]
parallel: yes
conflicts_with: []
issue: "#59"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#x402-and-the-agent-to-agent-economy"
created: 2026-10-19
updated: 2026-10-19
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

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
