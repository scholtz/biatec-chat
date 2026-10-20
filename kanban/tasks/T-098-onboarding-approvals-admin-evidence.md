---
id: T-098
title: "Onboarding: record operator tenant approvals in the admin evidence chain"
status: backlog
priority: P3
epic: "Sessions & auth"
blocked_by: [T-097]
area: ["backend/Biatec.Chat.Api/Onboarding/OnboardingEndpoints.cs", "backend/Biatec.Chat.Api/Onboarding/OnboardingService.cs", "backend/Biatec.Chat.Tests/Onboarding/**"]
parallel: yes
conflicts_with: []
issue: "#202"
branch: ""
owner: ""
taken_by: ""
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

- [ ] An unauthenticated POST with an unparseable body gets 401 (404 without keys), never 400 (test)
- [ ] An approved tenant appears in the admin chain with the operator name (test)

## Verification

`dotnet test --filter Onboarding|Admin`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 09:20 · coordinator · (new) → blocked · proposed by T-097
- 2026-10-20 06:59 · kanban-board · blocked → backlog · blockers done: T-097
