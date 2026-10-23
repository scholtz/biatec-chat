---
id: T-141
title: "Do not offer Bitcoin transfers until a PSBT signing path exists (BCHAT-037)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Core/Capabilities/CapabilityRegistry.cs", "backend/Biatec.Chat.Tests/Capabilities/ToolGateTests.cs"]
parallel: yes
conflicts_with: []
issue: "#290"
branch: "task/T-141"
owner: "session-20261023-1313"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-141 - Do not offer Bitcoin transfers until a PSBT signing path exists (BCHAT-037)

## Context

Security audit finding BCHAT-037 (Low, issue #290). `wallet.transfer` accepts `network: "bitcoin"` and the backend builds a Bitcoin preview
and persists it with evidence, but Confirm only signs Algorand groups (`PrepareGroup` refuses anything else), so every Bitcoin
preview fails as `group_mismatch`. Failing closed is safe, but the user sees a card they can never confirm and the evidence log records an action that
could not happen.

## Goal

The model cannot start a Bitcoin transfer; reading Bitcoin addresses and balances stays available.

## Scope

- In: `CapabilityRegistry.CreateDefault(bitcoinTransfers: false)` removes `bitcoin` from the `wallet.transfer` input schema (and its description) unless the flag is set; the tool gate then refuses it; tests.
- Out: the Bitcoin transfer service and PSBT code (kept, switched off by the schema), the PSBT signing path itself; BCHAT-041.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] With the default registry, `wallet.transfer` with network `bitcoin` is refused by the gate; mainnet and testnet are accepted.
- [x] `wallet.balance` and `wallet.listAddresses` still accept `bitcoin`.
- [x] `CreateDefault(bitcoinTransfers: true)` restores the old schema (used by the Bitcoin tests).
- [x] The full backend suite stays green.

## Verification

`dotnet test backend`.

## Hand-off

- **Summary:** CapabilityRegistry.CreateDefault(bitcoinTransfers: false) removes bitcoin from the wallet.transfer input schema and description, so the gate refuses it; the flag is configurable as Wallet:BitcoinTransfers (default false); WalletTransferExecutor also refuses bitcoin when the capability does not offer it (defence in depth). wallet.balance and wallet.listAddresses still accept bitcoin. The knowledge-base pages and architecture doc no longer claim Bitcoin transfers work.
- **Files changed:** Core/Capabilities/CapabilityRegistry.cs, Api/Chat/ChatEndpoints.cs, Api/Wallet/Transfer/WalletTransferExecutor.cs, Tests (ToolGate, WalletRead, Transfer), docs/kb/transfers.md, docs/kb/algorand-basics.md, docs/architecture.md. Also docs/kb/biatec-chat-assistant.md, docs/kb/action-previews.md, k8s/README.md (Wallet__BitcoinTransfers), audits/risks.md (RISK-023 mitigated).
- **How it was verified:** Full backend suite 1624 passed, 0 failed (new: gate refuses bitcoin transfer for the schema reason and records a policy event, flag restores it, reads of bitcoin still allowed, executor refuses without building a preview).
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** Turn Wallet__BitcoinTransfers on only together with a PSBT signing path (documented in k8s/README.md). A Bitcoin preview stored before this change would still reach Confirm and fail as group_mismatch (nothing was in production with Bitcoin enabled that I know of). #294 (BCHAT-041) concerns code that is now unreachable by default.

## Review

- 2026-10-23 14:13 · approve · PR #334 merged into main @ b95dba8 (recorded by kanban-sync)

## Activity log

- 2026-10-23 23:00 · claude · (new) → backlog · created from GitHub issue #290 (BCHAT-037); blockers scan: none
- 2026-10-23 13:13 · scholtz · taken, and session-20261023-1313 · backlog → in-progress · claimed
- 2026-10-23 14:13 · user · in-progress → done · PR #334 merged into main @ b95dba8 (synced by kanban-sync)
