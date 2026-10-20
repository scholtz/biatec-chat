---
id: T-090
title: "Bug: transfer preview must look up ASA decimals and unit name (real MCP balances carry none)"
status: backlog
priority: P1
epic: "Wallet tools"
blocked_by: [T-089]
area: ["backend/Biatec.Chat.Tools/Wallet/Transfer/**", "backend/Biatec.Chat.Tools/Algod/**", "backend/Biatec.Chat.Tests/Transfer/**"]
parallel: yes
conflicts_with: []
issue: "#170"
branch: ""
owner: ""
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-20
updated: 2026-10-20
---

# T-090 · Bug: transfer preview must look up ASA decimals and unit name (real MCP balances carry none)

## Context

Found by the T-089 worker. The real Biatec MCP `getCryptoBalance` response lists ASAs as `assets: [{assetId, amount}]`
with no decimals or unit name (sibling BiatecMCP `WalletApiModels.cs`, read only). The wallet.transfer preview refuses
rather than guess decimals, so against the real MCP every ASA transfer (for example USDC) is refused with "number of
decimals … is unknown"; only ALGO transfers work. MCP also lists at most 50 ASAs (`assetsTruncated`), so a source holding
more can be told it "does not hold asset N" and a receiver may get a needless opt-in step.

## Goal

ASA transfers preview with the right decimals and unit name against the real Biatec MCP.

## Scope

- In: look up asset params (decimals, unitName) through algod `GET /v2/assets/{id}` for the request's network
  (`AlgodClientFactory` exists), with a short cache; treat the unit name as untrusted data (sanitize, never as
  instructions); when `assetsTruncated` is set, confirm holding / opt-in for the specific asset through algod
  (`/v2/accounts/{addr}/assets/{id}`) instead of concluding from the truncated list.
- Out: Bitcoin; signing.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] An ASA transfer preview against a real-shape MCP fake shows the algod decimals and unit name (test)
- [ ] An algod lookup failure refuses with a clear message, never a guessed amount (test)
- [ ] With `assetsTruncated`, holding and opt-in are confirmed per asset via algod (test)

## Verification

`dotnet test --filter Transfer`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 05:50 · coordinator · (new) → blocked · proposed by T-089
- 2026-10-20 04:00 · kanban-board · blocked → backlog · blockers done: T-089
- 2026-10-20 04:00 · LukassSoukup · taken · status stays backlog
