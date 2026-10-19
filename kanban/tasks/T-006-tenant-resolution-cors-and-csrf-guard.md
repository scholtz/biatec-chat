---
id: T-006
title: Tenant resolution, CORS and CSRF guard
status: blocked
priority: P0
epic: "Sessions & auth"
blocked_by: [T-003]
area: ["backend/Biatec.Chat.Api/Tenancy/**", "backend/Biatec.Chat.Api/Security/**"]
parallel: yes
conflicts_with: []
issue: "#7"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-006 · Tenant resolution, CORS and CSRF guard

## Context

Tenant derived from Host header only; credentialed CORS with exact origin, never wildcard; state-changing endpoints require a custom header plus the tenant origin allow-list; clientKey is a label only.

## Goal

Every request knows its tenant and is origin-checked.

## Scope

- In: Tenant lookup by Host (seed the Biatec Chat tenant: chat.biatec.io, origins web.biatec.io, dex.biatec.io; plus localhost dev tenant), exact-origin CORS with credentials, CSRF middleware (custom header + Origin in allow-list) on non-GET, clientKey validation on cookie transport.
- Out: Self-service onboarding (T-045).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Unknown Host returns 404
- [ ] Allowed origin gets exact ACAO and ACAC true; other origin gets none
- [ ] POST without the custom header or from a foreign origin is 403
- [ ] Tests cover each case

## Verification

dotnet test --filter Tenancy

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
