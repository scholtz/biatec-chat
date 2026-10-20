---
id: T-086
title: "Bug: admin API for policy and knowledge-base management"
status: done
priority: P3
epic: "Orchestrator"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Tests/Admin/**", "docs/admin-api.md"]
parallel: yes
conflicts_with: []
issue: "#155"
branch: "task/T-086"
owner: "worker-T-086"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#responsibilities"
created: 2026-10-20
updated: 2026-10-20
---

# T-086 · Bug: admin API for policy and knowledge-base management

## Context

Spec, Chat Backend Responsibilities: *"Expose health, metrics, and an admin API for policy and knowledge-base
management."* Tenant soft cap: *"only a Biatec Chat operator may raise it for a tenant that chains to a recognized
trust anchor"*.

## Analysis

Recorded by the spec-vs-implementation audit on 2026-10-20 (`main @ 6b701e5`, finding #9).

- No admin endpoints exist (`grep -rn "/admin" backend/Biatec.Chat.Api` finds none); tenants are created only via
  onboarding or SQL (`k8s/README.md`), KB content lives in `docs/kb/` files.
- Operator-only actions the spec names: raising a tenant's soft cap, setting `trustAnchorVerified`, enabling
  `limitsUpdateEnabled` / `x402Enabled`, budgets, and KB management.
- Authentication for operators is the open design question: there are no IAM users; options are an operator OIDC
  group/claim at Biatec OIDC, or a network-restricted endpoint with a k8s-secret bearer. Must not weaken tenant
  isolation.
- Likely too large for the freeze; the analysis may approve a minimal subset (tenant policy read/update) or reject.

## Analysis prompt

Paste this into the session that picks the task up, before writing any code:

> Analyse task T-086 in biatec-chat. Read PRODUCT-DESCRIPTION.md Responsibilities, Tenant-level soft cap, Tenant row of
the Data Model, and the KB handling (Model Layer, kb.search). Inspect Onboarding/**, Tenancy/**, Tools/Kb/** and
docs/kb/. Propose the smallest admin API that covers operator-only policy changes and KB reload, how operators
authenticate without IAM users or shared secrets in code, and whether it should be done before the code freeze or
deferred (verdict REJECTED/deferred is acceptable).
>
> Report: (a) does the conflict still exist on current `main`, with file:line evidence; (b) is the spec text
> unambiguous, quoting it; (c) the smallest change that satisfies it and what it touches outside this task's `area`;
> (d) risks and tests needed. End with a verdict line: `APPROVED`, `PARTIAL: <approved items>` or
> `REJECTED: <reason>`, and write it into the task's Analysis verdict section.

## Analysis gate

This task starts with an analysis step. **No code may be written until the Analysis verdict below says `APPROVED`
or `PARTIAL`.**

1. Run the **Analysis prompt** above against the current `main` (the finding was recorded on `main @ 6b701e5`;
   later merges may already have fixed or changed it).
2. Write the result into **Analysis verdict**: `APPROVED` (implement as scoped), `PARTIAL` (list exactly which
   findings or acceptance criteria are approved; only those may be implemented, the rest are struck through with a
   reason), or `REJECTED` (no conflict with the spec after all, or already fixed; give the evidence).
3. `APPROVED` / `PARTIAL`: implement only the approved part, then hand off as usual.
   `REJECTED`: change no code; move the task to `review` with the evidence in the Hand-off, so the user can close it.
4. If the analysis shows the fix needs a spec decision (the spec is ambiguous or two spec sections disagree), record
   the question under Hand-off → Open questions and ask the user before implementing that part.

## Analysis verdict

PARTIAL, 2026-10-20 (worker, recorded by the coordinator). Approved and done: operator authentication, tenant policy
read and validated update (soft-cap floor, operator-only raise for trust-anchored tenants), audit logging, docs.
Deferred: knowledge-base reload/upload (needs a reloadable KbSearchService and shared storage across replicas).
Evidence: no admin endpoints on main; the only operator route was `POST /onboarding/operator/tenants`
(`Onboarding/OnboardingEndpoints.cs:80-89`, bearer `Onboarding:OperatorKey`); `SoftCapPolicy.ValidateOnSave` was only
called with `byOperator:false` (`OnboardingService.cs:281`); `KbSearchService` is an immutable singleton
(`Tools/Kb/KbSearchService.cs:24-26`). Spec `PRODUCT-DESCRIPTION.md:302` and `:387`.

## Goal

Operators can read and change tenant policy and manage the knowledge base through an authenticated admin API.

## Scope

- In: operator authentication, tenant policy endpoints with server-side validation (soft-cap floor, model rule), KB
  reload/upload, evidence of changes, docs.
- Out: a UI.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] The Analysis verdict is recorded (APPROVED / PARTIAL / REJECTED) with evidence
- [x] Admin endpoints reject non-operators
- [x] Raising a soft cap is refused for a tenant without `trustAnchorVerified`
- [x] Every change is logged without secrets
- [x] docs/admin-api.md documents the endpoints

## Implementation notes

Never return `clientSecretRef` targets or secrets.

## Verification

`dotnet test --filter Admin`.

## Hand-off

- **Summary:** operator admin API under `/onboarding/operator/tenants`: list, read, `PATCH …/{id}/policy` (softCapEurPerHour, trustAnchorVerified, limitsUpdateEnabled, x402Enabled, bedrockUsdPerWindow, safeApprovalCaps, defaultModel; reason required). Auth (shared `OperatorAuth`: bearer `Onboarding:OperatorKey` from a secret, constant-time, 404 when unset) runs before the body is read; own `operator` rate-limit partition. Updates validate only the changed rules (soft-cap floor and operator-only raise for trust-anchored tenants) under an advisory lock, refuse unknown/identity/secret fields, evict the tenant cache (TTL now 10 s on other pods), and write a sanitized audit log line; responses never contain secrets.
- **Branch:** `task/T-086` @ `c598562` (merged with main), PR #188 (`Closes #155`).
- **Files changed:** `Admin/**`, `Tests/Admin/**`, `docs/admin-api.md`; outside the area: `Program.cs`, `Onboarding/OperatorAuth.cs` (new) + `OnboardingEndpoints.cs`, `Tenancy/TenantStore.cs` (Invalidate, TTL 10 s).
- **How it was verified:** Admin + Onboarding + Tenancy 95 passed; full backend on current main 1191 passed, 1 skipped, 1 flaky (`SessionProfileTests.Fill_IsSingleFlightedPerSession`, timing-based, passes 3/3 alone; follow-up T-095). Review (medium): all realistic findings fixed.
- **Known limitations:** `trustAnchorVerified` is the operator's attestation (no federation chain check); one shared operator key (no per-person identity); `consentThresholdCeilingEur` read-only; `defaultModel` takes effect once T-085 ships; the operator onboarding route still binds its body before auth.
- **Follow-up tasks proposed:** knowledge-base reload/upload; per-operator identity and evidence for admin changes.
- **Unblocks:** no task is blocked by T-086.
- **Open questions:** see known limitations (trust anchor attestation, shared key).

## Review

- 2026-10-20 05:30 · approve · PR #188 merged into main @ 0c48f10 (recorded by kanban-sync)

## Activity log

- 2026-10-20 02:40 · coordinator · (new) → backlog · created from the spec-vs-implementation audit of main @ 6b701e5 (finding #9)
- 2026-10-20 04:44 · LukassSoukup · taken · status stays backlog
- 2026-10-20 04:44 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 05:30 · coordinator · in-progress → review · branch task/T-086 @ c598562, PR #188; analysis verdict PARTIAL recorded
- 2026-10-20 05:30 · user · review → done · PR #188 merged into main @ 0c48f10 (synced by kanban-sync)
