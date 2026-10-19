---
id: T-010
title: Scope elevation for sign and manage-limits
status: blocked
priority: P0
epic: "Sessions & auth"
blocked_by: [T-009]
area: ["backend/Biatec.Chat.Api/Auth/Elevation/**"]
issue: "#11"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-010 · Scope elevation for sign and manage-limits

## Context

/auth/elevate accepts only sign and manage-limits, requests the union with held scopes, vaults new tokens first; revocation endpoint absent so the old entry is replaced and ages out.

## Goal

Incremental consent in the same popup mechanism.

## Scope

- In: /auth/elevate with allow-list and union, same state/PKCE handling, scope check helper used before signing or limits calls, error to offer re-authorization.
- Out: Revocation (not available at OIDC yet).

## Acceptance criteria

- [ ] rekey or any other scope is rejected
- [ ] Requested scope is union of held + new
- [ ] Scope helper refuses signing without sign and returns a re-auth hint
- [ ] Tests cover all of the above

## Verification

dotnet test --filter Elevation

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
