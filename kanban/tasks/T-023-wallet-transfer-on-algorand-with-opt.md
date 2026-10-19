---
id: T-023
title: wallet.transfer on Algorand with opt-in steps
status: blocked
priority: P0
epic: "Wallet tools"
blocked_by: [T-020, T-022]
area: ["backend/Biatec.Chat.Tools/Wallet/Transfer/**"]
parallel: yes
conflicts_with: []
issue: "#24"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-023 · wallet.transfer on Algorand with opt-in steps

## Context

Transfers of ALGO or any ASA via createPaymentTransaction; if the receiver is the user's own un-opted address: optional ALGO funding, then opt-in from that address, then transfer; steps listed but built one at a time.

## Goal

Users can move funds from chat with a preview that matches the chain.

## Scope

- In: Executor for wallet.transfer (Algorand family), own-address opt-in detection, multi-step sequencing on top of T-021/T-022.
- Out: Bitcoin (T-024).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Simple ALGO and ASA transfer works on testnet
- [ ] Own un-opted receiver produces a numbered multi-step card
- [ ] Later step built only after earlier step confirms
- [ ] Preview matches the landed transaction

## Verification

dotnet test --filter Transfer; testnet manual run with stage identity

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
