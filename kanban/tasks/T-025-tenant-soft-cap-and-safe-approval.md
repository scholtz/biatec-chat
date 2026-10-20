---
id: T-025
title: Tenant soft cap and safe approval caps
status: review
priority: P2
epic: "Wallet tools"
blocked_by: [T-022]
area: ["backend/Biatec.Chat.Tools/Caps/**"]
parallel: no
conflicts_with: [T-024, T-035, T-049, T-079, T-087]
issue: "#26"
branch: "task/T-025"
owner: "worker-T-025"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-20
---

# T-025 · Tenant soft cap and safe approval caps

## Context

Rolling-window soft cap (default 1500 EUR/h, floor = consent ceiling) with atomic reservation; above it set requireConsent; safe approvals capped 5/h and 2000 EUR/h, executions 10/h.

## Goal

Damage limiter against a hostile host page.

## Scope

- In: SoftCapSpend reservations and finalization, release on expiry/cancel/deny/fail/step-up, SafeApprovalCount caps, requireConsent flag on sign requests, pruning.
- Out: OIDC-side enforcement.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-024, T-035, T-049: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] Concurrent reservations cannot exceed the cap
- [x] Release paths tested
- [x] requireConsent set while window total exceeds cap
- [x] Safe caps enforced per kind

## Verification

dotnet test --filter Caps

## Hand-off

- **Summary:** `ISpendCaps`/`SpendCaps`: reservation in PostgreSQL under a per-(tenant, OIDC sub) advisory lock against a 1-hour rolling window; over the cap → requireConsent (OR-ed with T-033's per-preview flag); released on step-up, refusal, outage, signing errors and nothing-moved failures; finalized with OIDC's valuedEur on signing; phone-approved actions not counted. Safe caps by capability name: approve 5/h and 2000 EUR/h (value from max(predicted, valuedEur)), execute 10/h; in-flight safe actions are reserved rows freed by the pruner. Fail closed: requireConsent requested but OIDC signed without a challenge → not submitted (`consent_not_enforced`). `Caps:OidcHonoursRequireConsent` (default false) turns forced consent into a hard refusal against real Biatec OIDC (AGENTS.md rule, required-changes row 3). Tenant save validates the cap floor. Evidence records requireConsent and the cap reason.
- **Branch:** `task/T-025` @ `c2a2a6f` (merged with main), PR #161 (`Closes #26`).
- **Files outside the area:** `ConfirmService` (reserve/release/finalize around signing, fail-closed check), `OnboardingService` (validate on save), `Program.cs`, `docs/required-changes-biatec-oidc.md` (row 3 fallback), one assertion in `SafeProposalFlowTests`.
- **Verification:** Caps 33 passed; full backend twice 991 passed, 1 skipped, 0 failed.
- **Review (high):** fixed fail-open with an OIDC that ignores requireConsent, the missing config switch, safe approval value never accruing, cancelled safe rows never released, finalize-before-submit counting failed sends, evidence before the cap decision. Known limitations: ValidateOnSave has no tenant-update caller; pruner loop duplicates the sweeper's; several queries inside the lock; an aborted signing request keeps its reservation until the step expires; phone-approved safe actions still count toward safe caps.
- **Open questions:** `Caps:OidcHonoursRequireConsent` stays false in all configs (Development talks to real Biatec OIDC). With it false, T-033's forced consent for threshold-1 safe proposals is refused against real OIDC until required-changes row 3 ships.
- **Unblocks:** see `board.py unblocks T-025`.

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022
- 2026-10-19 23:24 · LukassSoukup · backlog · conflicts_with revised: dropped T-057: OTel spans come from HttpClient/AWS SDK instrumentation at registration
- 2026-10-20 02:26 · LukassSoukup · taken · status stays backlog
- 2026-10-20 02:26 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 03:25 · coordinator · in-progress → review · branch task/T-025 @ c2a2a6f, PR #161
