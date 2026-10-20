---
id: T-024
title: wallet.transfer on Bitcoin (PSBT intent hash)
status: done
priority: P1
epic: "Wallet tools"
blocked_by: [T-020, T-022, T-023]
area: ["backend/Biatec.Chat.Tools/Wallet/Bitcoin/**"]
parallel: no
conflicts_with: [T-025, T-035, T-049, T-061]
issue: "#25"
branch: "task/T-024"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-20
---

# T-024 · wallet.transfer on Bitcoin (PSBT intent hash)

## Context

Bitcoin uses PSBT with prevout data; hash = SHA-256 over JCS of {version, locktime, inputs, outputs, feeSats}; preview expires after 30 min or if inputs are spent or fee rate moves over 25%; one sender address per transaction.

## Goal

Transfers between the user's Bitcoin addresses.

## Scope

- In: createBitcoinTransaction wrapper usage, PSBT decoding, intent hash, expiry and invalidation checks, per-address split.
- Out: Multi-input-address transactions in one signing call.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-025, T-035, T-049, T-061: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Intent hash matches a fixed test vector
- [ ] Preview shows inputs, outputs, change, fee rate
- [ ] Spent input invalidates preview
- [ ] Test against testnet or fixtures

## Verification

dotnet test --filter Bitcoin

## Hand-off

## Review

- 2026-10-20 02:18 · approve · PR #139 merged into main @ 748ee89 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-020: transfers between own Bitcoin addresses need address listing (same as T-023)
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-020, T-022
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped T-057: OTel spans come from HttpClient/AWS SDK instrumentation at registration
- 2026-10-20 01:09 · LukassSoukup · backlog · parallel analysis: blocked_by + T-023 (Bitcoin extends T-023's wallet.transfer executor in Api/Wallet/Transfer); added T-061 (ConfirmService)
- 2026-10-20 01:47 · scholtz · taken · status stays backlog
- 2026-10-20 01:47 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 02:18 · user · in-progress → done · PR #139 merged into main @ 748ee89 (synced by kanban-sync)
