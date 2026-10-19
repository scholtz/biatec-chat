---
id: T-019
title: Biatec MCP client with lazy tool cache
status: in-progress
priority: P0
epic: "Wallet tools"
blocked_by: [T-008]
area: ["backend/Biatec.Chat.Tools/Mcp/**"]
parallel: no
conflicts_with: [T-057]
issue: "#20"
branch: "task/T-019"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-19
updated: 2026-10-19
---

# T-019 · Biatec MCP client with lazy tool cache

## Context

MCP client to https://mcp.biatec.io/tools with the user's bearer; tool list cached per tenant and scope set for an hour, refreshed piggyback; alert if a needed tool disappears; never call signTransaction.

## Goal

Typed access to the Biatec MCP tools the capabilities need.

## Scope

- In: ModelContextProtocol C# client over streamable HTTP, bearer from vault with refresh on demand, cache keyed by tenant+scopes, typed wrappers for listAlgorandAddresses, getCryptoAddress, getCryptoBalance, createPaymentTransaction, createOptInTransaction, createBitcoinTransaction, createSwapTransaction, submitTransactionToBlockchain; signTransaction explicitly blocked.
- Out: Capabilities using it.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-057: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Wrappers work against a fake MCP server in tests
- [ ] openid-only cache never served to sign-scoped session
- [ ] Missing required tool raises an alert/log
- [ ] signTransaction cannot be invoked through the client

## Verification

dotnet test --filter Mcp

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:41 · kanban-board · blocked → backlog · blockers done: T-008
- 2026-10-19 19:42 · scholtz · taken · status stays backlog
- 2026-10-19 19:42 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
