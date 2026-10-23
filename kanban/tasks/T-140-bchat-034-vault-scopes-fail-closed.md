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

- [x] Login with a token response that has no `scope` leaves the session with only `openid` in the vault (no `sign`, no `email`).
- [x] Elevation with a token response that has no `scope` is refused (400, status Failed) and leaves the vault entry untouched.
- [x] Existing auth and elevation tests stay green.

## Verification

`dotnet test backend --filter "Auth|Elevation"`, then the full suite.

## Hand-off

- **Summary:** A token response without a scope no longer lets the requested scopes stand in for the granted ones: the login callback vaults only openid, and the elevation callback refuses (400, status Failed) and leaves the vault entry untouched. An empty scope string counts as omitted; a login without scope logs a warning. The refresh path keeps the previous scopes (RFC 6749 section 6: omitted means unchanged), which is deliberate.
- **Files changed:** Api/Auth/AuthEndpoints.cs, Api/Auth/Elevation/ElevationEndpoints.cs, Tests/Auth/MockOidc.cs (OmitScope switch), Tests/Auth/ElevationTests.cs.
- **How it was verified:** Full backend suite 1619 passed, 0 failed (3 new tests: login omitting scope vaults openid only, elevation omitting scope refused, elevation with an empty scope refused).
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** RFC 6749 lets a server omit scope when it equals the request, so a compliant server that does so now yields an openid-only session after login (the user elevates sign when needed) and cannot elevate until it returns scope. Biatec OIDC's mock always returns it; confirm the real one does.

## Review

## Activity log

- 2026-10-23 22:00 · claude · (new) → backlog · created from GitHub issue #287 (BCHAT-034); blockers scan: none
- 2026-10-23 12:57 · scholtz · taken, and session-20261023-1257 · backlog → in-progress · claimed
