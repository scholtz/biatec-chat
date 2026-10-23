---
id: T-140
title: "Fail closed when the token response omits its scope (BCHAT-034)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Api/Auth/Elevation/ElevationEndpoints.cs", "backend/Biatec.Chat.Tests/Auth/MockOidc.cs", "backend/Biatec.Chat.Tests/Auth/ElevationTests.cs"]
parallel: yes
conflicts_with: []
issue: "#287"
branch: "task/T-140"
owner: "session-20261023-1257"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-140 - Fail closed when the token response omits its scope (BCHAT-034)

## Context

Security audit finding BCHAT-034 (Low, issue #287). When the OIDC token response has no `scope`, the callback vaults the scopes the
backend requested, and the elevation callback treats the requested scope as granted. The vault's scope list gates `sign` and
`manage-limits`; a user who declined a scope, or a server that narrows one away silently, would still be recorded as holding it. Server-side
signing is also checked by Biatec OIDC, so this is defence in depth.

## Goal

A scope is recorded only when the server said it was granted.

## Scope

- In: login callback stores `["openid"]` when the response omits `scope`; the elevation callback refuses when the response omits `scope`; tests with the mock OIDC omitting it.
- Out: reading the scope claim from the access token (opaque tokens cannot be relied on).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] Login with a token response that has no `scope` leaves the session with only `openid` in the vault (no `sign`, no `email`).
- [ ] Elevation with a token response that has no `scope` is refused (400, status Failed) and leaves the vault entry untouched.
- [ ] Existing auth and elevation tests stay green.

## Verification

`dotnet test backend --filter "Auth|Elevation"`, then the full suite.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 22:00 · claude · (new) → backlog · created from GitHub issue #287 (BCHAT-034); blockers scan: none
- 2026-10-23 12:57 · scholtz · taken, and session-20261023-1257 · backlog → in-progress · claimed
