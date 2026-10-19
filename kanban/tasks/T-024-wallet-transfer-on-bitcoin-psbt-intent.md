---
id: T-024
title: wallet.transfer on Bitcoin (PSBT intent hash)
status: blocked
priority: P1
epic: "Wallet tools"
blocked_by: [T-020, T-022]
area: ["backend/Biatec.Chat.Tools/Wallet/Bitcoin/**"]
issue: "#25"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-19
---

# T-024 · wallet.transfer on Bitcoin (PSBT intent hash)

## Context

Bitcoin uses PSBT with prevout data; hash = SHA-256 over JCS of {version, locktime, inputs, outputs, feeSats}; preview expires after 30 min or if inputs are spent or fee rate moves over 25%; one sender address per transaction.

## Goal

Transfers between the user's Bitcoin addresses.

## Scope

- In: createBitcoinTransaction wrapper usage, PSBT decoding, intent hash, expiry and invalidation checks, per-address split.
- Out: Multi-input-address transactions in one signing call.

## Acceptance criteria

- [ ] Intent hash matches a fixed test vector
- [ ] Preview shows inputs, outputs, change, fee rate
- [ ] Spent input invalidates preview
- [ ] Test against testnet or fixtures

## Verification

dotnet test --filter Bitcoin

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-020: transfers between own Bitcoin addresses need address listing (same as T-023)
