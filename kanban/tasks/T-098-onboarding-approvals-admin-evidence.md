---
id: T-098
title: "Onboarding: record operator tenant approvals in the admin evidence chain"
status: review
priority: P3
epic: "Sessions & auth"
blocked_by: [T-097]
area: ["backend/Biatec.Chat.Api/Onboarding/OnboardingEndpoints.cs", "backend/Biatec.Chat.Api/Onboarding/OnboardingService.cs", "backend/Biatec.Chat.Tests/Onboarding/**"]
parallel: yes
conflicts_with: []
issue: "#202"
branch: "task/T-098"
owner: "worker-T-098"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-20
updated: 2026-10-20
---

# T-098 · Onboarding: record operator tenant approvals in the admin evidence chain

## Context

Proposed by the T-097 worker. T-097 records every admin change in a signed admin chain with the named operator, but
`POST /onboarding/operator/tenants` (operator approval of a tenant) accepts the named keys without being recorded, and
it binds its body before the auth check (unauthenticated requests with a bad body get 400 instead of 401).

## Goal

Every operator action, including tenant approval, is attributable and verifiable, and the route authenticates before
reading the body.

## Scope

- In: authenticate before body binding (as T-086/T-097 do); append an `admin_change` entry (operator, action
  `tenant.approve`, target host/tenant id, reason, the created policy values; never secrets) in the same transaction.
- Out: changes to the onboarding challenge flow.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] An unauthenticated POST with an unparseable body gets 401 (404 without keys), never 400 (test)
- [x] An approved tenant appears in the admin chain with the operator name (test)

## Verification

`dotnet test --filter Onboarding|Admin`, then the full backend suite.

## Hand-off

- **Summary:** `POST /onboarding/operator/tenants` authenticates (`OperatorAuth.Check`) before reading the body; a successful approval appends an `admin_change` (`tenant_approve`, snake_case like the other actions) with the named operator, target, optional reason and the created configuration/policy values (never clientSecretRef or clientKeys) in the same transaction as the tenant insert, so a failed append rolls it back. IEvidenceLog is resolved only after auth, so public onboarding routes don't depend on the evidence key store.
- **Branch:** `task/T-098`, PR #205 (`Closes #202`).
- **Files outside the area:** `Admin/AdminEvidence.cs` (one constant), `docs/admin-api.md`, `docs/required-changes-biatec-oidc.md` (operator approval wording).
- **Verification:** Onboarding + Admin 98 passed; full backend 1276 passed, 1 skipped, 0 failed.
- **Unblocks:** no task is blocked by T-098.

## Review

## Activity log

- 2026-10-20 09:20 · coordinator · (new) → blocked · proposed by T-097
- 2026-10-20 06:59 · kanban-board · blocked → backlog · blockers done: T-097
- 2026-10-20 07:00 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:00 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 07:15 · coordinator · in-progress → review · PR #205
