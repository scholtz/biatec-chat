---
id: T-045
title: Tenant onboarding API with domain verification
status: backlog
priority: P3
epic: "Sessions & auth"
blocked_by: [T-006]
area: ["backend/Biatec.Chat.Api/Onboarding/**"]
parallel: yes
conflicts_with: []
issue: "#46"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#embedding-modes"
created: 2026-10-19
updated: 2026-10-19
---

# T-045 · Tenant onboarding API with domain verification

## Context

Self-service onboarding binds a host after DNS TXT or federation proof; registers a confidential client (fallback: operator adds static client); secret stored in secret store by reference.

## Goal

Third-party tenants without code changes.

## Scope

- In: Onboarding endpoints, DNS TXT challenge check, tenant record with clientSecretRef, operator approval fallback, origin proofs.
- Out: OpenID Federation evaluation.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] TXT challenge verified before host binds
- [ ] Host can belong to one tenant only
- [ ] Secret never stored in DB
- [ ] Tests with fake DNS

## Verification

dotnet test --filter Onboarding

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:41 · kanban-board · blocked → backlog · blockers done: T-006
