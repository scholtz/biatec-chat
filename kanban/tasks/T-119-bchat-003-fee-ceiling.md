---
id: T-119
title: "Enforce a fee ceiling on swap, liquidity, safe-creation and x402 groups (BCHAT-003)"
status: in-progress
priority: P1
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Tools/Dex/**", "backend/Biatec.Chat.Tools/Safe/Create/**", "backend/Biatec.Chat.Tools/X402/**", "backend/Biatec.Chat.Tests/Dex/**", "backend/Biatec.Chat.Tests/X402/**", "backend/Biatec.Chat.Tests/Safe/**"]
parallel: yes
conflicts_with: []
issue: "#256"
branch: "task/T-119"
owner: "session-20261022-1700"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-119 - Enforce a fee ceiling on swap, liquidity, safe-creation and x402 groups (BCHAT-003)

## Context

Security audit finding BCHAT-003 (High, issue #256). Transfers (`TransferStepBuilder`, 10,000 µALGO) and safe proposals
(`SafeProposalBuilder`) cap `tx.Fee`; `SwapGroupVerifier`, `LiquidityGroupVerifier` (T-118), `SafeBuilder` and
`X402PayService` do not, so a builder can choose a fee that drains ALGO. The CLAMM library legitimately uses static fees
of 8,000 (add liquidity) and 12,000 (remove liquidity) µALGO on the pool call.

## Goal

Every transaction of these groups is refused when its fee exceeds a documented per-transaction ceiling.

## Scope

- In: per-transaction fee ceilings in the four verifiers (plain transfers 10x min fee; app calls with inner
  transactions a documented larger allowance); tests per path.
- Out: showing `FeeAmount` on the swap and x402 cards (widget contract change; follow-up); other findings (BCHAT-010, BCHAT-011).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Swap refuses a group with `Fee = 5_000_000` on any transaction (`SwapRefused`).
- [x] Liquidity refuses an inflated fee; honest add (8,000) and remove (12,000) still pass.
- [x] x402 refuses a 1 ALGO fee on the transfer.
- [x] Safe creation refuses a 10 ALGO fee on the deploy call.
- [x] Existing tests stay green; audit TEST-015 passes.

## Verification

`dotnet test backend`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-015.sh`.

## Hand-off

- **Summary:** GroupFees (per-transaction and per-group ceilings) enforced in the swap, liquidity, safe-create and x402 verifiers; TransferStepBuilder shares the constant. PR #301.
- **Files changed:** Tools/Algod/GroupFees.cs, Dex/Swap, Dex/Liquidity, Safe/Create, X402, Wallet/Transfer (constant), and their tests.
- **How it was verified:** dotnet test 1525 passed (incl. real sidecar safe-create fixtures); TEST-015 passes.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The finding also asks for `FeeAmount` on the swap and x402 cards (widget contract change). I left it out of this task; do you want a follow-up task?

## Review

## Activity log

- 2026-10-22 17:00 · claude · (new) → backlog · created from GitHub issue #256 (BCHAT-003); blockers scan: none
- 2026-10-22 17:01 · scholtz · taken, and session-20261022-1700 · backlog → in-progress · claimed
