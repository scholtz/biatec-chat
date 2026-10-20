---
id: T-090
title: "Bug: transfer preview must look up ASA decimals and unit name (real MCP balances carry none)"
status: done
priority: P1
epic: "Wallet tools"
blocked_by: [T-089]
area: ["backend/Biatec.Chat.Tools/Wallet/Transfer/**", "backend/Biatec.Chat.Tools/Algod/**", "backend/Biatec.Chat.Tests/Transfer/**"]
parallel: yes
conflicts_with: []
issue: "#170"
branch: "task/T-090"
owner: "worker-T-090"
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

- [x] An ASA transfer preview against a real-shape MCP fake shows the algod decimals and unit name (test)
- [x] An algod lookup failure refuses with a clear message, never a guessed amount (test)
- [x] With `assetsTruncated`, holding and opt-in are confirmed per asset via algod (test)

## Verification

`dotnet test --filter Transfer`, then the full backend suite.

## Hand-off

- **Summary:** shared `IAlgodAssetReader` / `AlgodAssetReader` (Tools/Algod): ASA decimals and unit name from algod `/v2/assets/{id}` (10 min cache, case-insensitive network key, failures not cached) and per-asset holding from `/v2/accounts/{addr}/assets/{id}` (only algod's "account asset info not found" 404 means not opted in; any other 404 is a failed read). The transfer preview takes ASA decimals and symbol only from algod; failures refuse, never guess; an unconfigured reader gets its own refusal and an error log. With `assetsTruncated`, holding and opt-in are confirmed per asset. ASAs are always labelled with their id ("USDC (ASA 10458941)"), so a scam ASA named "ALGO" can't pass as ALGO. One untrusted-text cleaner `UntrustedText.Clean` (strips Cc, Cf incl. bidi overrides and zero-width, separators, private-use, lone surrogates, quotes, backticks, `<>\\`) used for unit names and safe proposal text.
- **Branch:** `task/T-090` @ `1299a26` (merged with main), PR #177 (`Closes #170`).
- **Files outside the area:** `Tools/Safe/Proposals/SafeProposalService.cs` (`Sanitize` delegates to `UntrustedText.Clean`).
- **Verification:** Transfer + SafeProposal 134 passed; full backend 1144 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed.
- **Known limitations:** serial algod calls in the preview; raw HttpClient per network instead of IHttpClientFactory; `AlgodSafeHoldingsReader` still separate (T-093).
- **Open questions:** asset rows carry the full label in `AssetAmount.Symbol` next to `AssetId`; if the widget prints the id beside the symbol, it shows twice (cosmetic).
- **Unblocks:** T-093.

## Review

- 2026-10-20 04:31 · approve · PR #177 merged into main @ 7a276a2 (recorded by kanban-sync)

## Activity log

- 2026-10-20 05:50 · coordinator · (new) → blocked · proposed by T-089
- 2026-10-20 04:00 · kanban-board · blocked → backlog · blockers done: T-089
- 2026-10-20 04:00 · LukassSoukup · taken · status stays backlog
- 2026-10-20 04:00 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 04:31 · coordinator · in-progress → review · branch task/T-090 @ 1299a26, PR #177
- 2026-10-20 04:31 · user · review → done · PR #177 merged into main @ 7a276a2 (synced by kanban-sync)
