---
id: T-033
title: safe.propose, approve and execute capabilities
status: review
priority: P1
epic: "Safe"
blocked_by: [T-023, T-030, T-032]
area: ["backend/Biatec.Chat.Tools/Safe/Proposals/**"]
parallel: yes
conflicts_with: []
issue: "#34"
branch: "task/T-033"
owner: "worker-T-033"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#safe-capabilities"
created: 2026-10-19
updated: 2026-10-20
---

# T-033 · safe.propose, approve and execute capabilities

## Context

Propose payment, axfer or DEX add-liquidity payload (funding as separate transfers); approve from another user's session with expectedPayloadVersion; execute once threshold met; admin changes are guided proposal sequences; propose and approve are separate signing calls.

## Goal

Demo steps 2 to 7.

## Scope

- In: Executors, payload preview with version, guided multi-step sequences, execution with step-up handling via T-022.
- Out: x402.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] Opt-in proposal flow works for two users
- [ ] Liquidity proposal approved 2 of 2 and executed on testnet
- [x] Changed payload invalidates approval
- [x] Tests with fakes

## Verification

dotnet test --filter SafeProposals; testnet demo run

## Hand-off

- **Summary:** safe.propose (payment, asset transfer, opt-in, Biatec DEX add-liquidity, admin changes incl. guided createGroup), safe.approve bound to `expectedPayloadVersion`, safe.execute; all through the T-022 confirm path with step-up → consent → resume. Every sidecar result is decoded from the unsigned bytes, checked against the request and re-encoded byte for byte; DEX app ids come from a backend table (`BiatecDexApps`), and the pool call must be exactly `addLiquidity` with the expected args. Rekey proposals are refused; asset config, key registration, close-to, app calls and unknown entries fail closed (value-moving, warning, phone approval predicted). Value-moving proposals in threshold-1 groups force `RequireConsent` on the first signing call.
- **Finding:** algo-safe records the proposer's approval inside the propose call (no separate "approve as first signer" step); Biatec OIDC values propose at zero (docs/required-changes-biatec-oidc.md row 5).
- **Branch:** `task/T-033` @ `8efb326` (merged with main), PR #159 (`Closes #34`).
- **Files outside the area:** `Program.cs`, `CapabilityRegistry` (input schemas), `ConfirmService` (first signing call passes `RequireConsent: PreviewRequiresConsent(preview)`; T-025 must OR its soft-cap decision into it), sidecar `safe/builders.ts` (liquidity payload source, payloadHash, inner-fee allowance only for `poolAppIds`) and `clamm/builders.ts`, docs/required-changes-biatec-oidc.md.
- **Verification:** backend 955 passed, 1 skipped, 0 failed (SafeProposal 22); sidecar 61 passed, 2 skipped; `tsc --noEmit` clean.
- **Review (high):** fixed all 8 findings (fail-closed payload review, threshold-1 consent, backend-held DEX ids and pool-method check, reuse of open previews only, ASA decimals, group choice after the pool, live status before execute reuse, fee allowance only for pools). Known limitations: duplicated Fee()/Sanitize helpers; `BiatecDexApps` copied from biatec-concentrated-liquidity-amm 0.9.44 (update on redeploy).
- **Not met:** the live 2-of-2 liquidity proposal and execution on testnet (needs live testnet; covered by T-059). The execution caps (10/h) and approval caps are T-025.
- **Open questions:** threshold ≥ 2 proposers' own approval is still valued at zero by Biatec OIDC; docs/hackathon-demo.md still describes a separate first-signer approval.
- **Unblocks:** see `board.py unblocks T-033`: T-059 still waits on T-036.

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added T-023: safe funding and box top-ups are separate wallet.transfer steps
- 2026-10-20 01:57 · kanban-board · blocked → backlog · blockers done: T-023, T-030, T-032
- 2026-10-20 01:58 · LukassSoukup · taken · status stays backlog
- 2026-10-20 01:58 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 02:55 · coordinator · in-progress → review · branch task/T-033 @ 8efb326, PR #159
