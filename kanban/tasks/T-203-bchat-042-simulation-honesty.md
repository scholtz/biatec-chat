---
id: T-203
title: "Never claim a simulation that did not run; a requested sidecar simulation that is missing is a failed build (BCHAT-042)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Dex/Liquidity/**", "backend/Biatec.Chat.Tools/Safe/**", "backend/Biatec.Chat.Tests/Dex/Liquidity/**", "backend/Biatec.Chat.Tests/Safe/**"]
parallel: yes
conflicts_with: []
issue: "#295"
branch: "task/T-203"
owner: "session-20261023-1224"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-203 - Never claim a simulation that did not run; a requested sidecar simulation that is missing is a failed build (BCHAT-042)

## Context

Security audit finding BCHAT-042 (Low, issue #295, TLS-6). The liquidity preview summary hard-codes "simulation passed" although liquidity groups are never simulated. For the algo-safe sidecar builders the backend sends `simulate: true`; if the sidecar leaves the `simulation` object out, the build is accepted with no simulation and the preview shows none.

## Goal

A preview never says a simulation passed unless one ran and succeeded. A safe build that asked for a simulation and got none is refused.

## Scope

- In: liquidity previews simulate on algod when an endpoint is configured (refused when it fails; otherwise the card says it was not simulated); the two safe builders refuse a sidecar build without the requested `simulation` object; tests.
- Out: the swap and transfer flows, which already say "Not simulated" when they cannot (kept as is).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] A liquidity preview whose group fails the algod simulation is refused; one that passes says what algod reported; with no endpoint or an unreachable algod the card says it was not simulated. The text "simulation passed" appears nowhere unless algod reported success.
- [x] A safe create or proposal build answered without a `simulation` object is refused (nothing previewed).
- [x] Existing liquidity and safe tests stay green, with new tests for the three outcomes.

## Verification

`dotnet test backend --filter "Liquidity|Safe"`, then the full backend suite (CI).

## Hand-off

- **Summary:** liquidity previews (`dex.addLiquidity`, `dex.removeLiquidity`) now simulate on algod when an endpoint is configured for the network: a failing simulation refuses the preview, a passing one shows algod's own summary, no endpoint or an unreachable algod shows "Not simulated: ..." (the hard-coded "simulation passed" is gone). `SafeBuilder` and `SafeProposalBuilder` refuse a sidecar build that was asked to simulate (`simulate: true` is on every call) and came back without a `simulation` object. Swap and transfer already said "Not simulated" and are unchanged.
- **Verification:** `dotnet test --filter Liquidity|Safe`: 75 and 81 pass, with 5 new tests (no endpoint, success, failure, unreachable, missing safe simulation). The full suite runs in the PR's CI.

## Review

## Activity log

- 2026-10-23 12:24 · kanban-split · (new) → backlog · created from audit issue #295
- 2026-10-23 12:24 · scholtz · taken · status stays backlog
- 2026-10-23 12:24 · session-20261023-1224 · backlog → in-progress · claimed, branch task/T-203
