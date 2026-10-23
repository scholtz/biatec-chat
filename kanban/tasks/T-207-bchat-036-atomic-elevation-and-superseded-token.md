---
id: T-207
title: "Replace the vault entry atomically on elevation and revoke or park the superseded refresh token (BCHAT-036)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Vault/TokenVault.cs", "backend/Biatec.Chat.Api/Auth/Elevation/**", "backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Tests/Vault/**", "backend/Biatec.Chat.Tests/Auth/ElevationTests.cs"]
parallel: yes
conflicts_with: []
issue: "#289"
branch: "task/T-207"
owner: "session-20261023-1320"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-207 - Replace the vault entry atomically on elevation and revoke or park the superseded refresh token (BCHAT-036)

## Context

Security audit finding BCHAT-036 (Low, issue #289, IDA-6, RISK-002). `ElevationCompletion` reads the vault entry, exchanges the code, re-reads the entry and writes the elevated tokens with a plain `PutAsync`, outside the Redis lock that serialises refreshes. A refresh that rotated the refresh token in between is silently overwritten, and the refresh token the elevation replaced is neither revoked nor parked, so it lives on at Biatec OIDC until it expires (PRODUCT-DESCRIPTION.md: the replaced token is revoked or parked in PendingRevocation).

## Goal

An elevation replaces the entry while holding the vault's refresh lock (re-reading and re-checking inside it), and the refresh token it supersedes is revoked, or parked for the sweeper to revoke.

## Scope

- In: `ITokenVault.ReplaceAsync` (locked read-modify-write; a default implementation for the in-memory fakes), its use in the elevation completion, revoking or parking the superseded refresh token, tests; the stale comment in the elevation completion.
- Out: the logout and sweeper paths (already revoke or park).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] The elevated entry is written under the same Redis lock the refresh uses, from the entry read inside it; a refresh and an elevation never overwrite each other.
- [x] If the entry changed so that the elevation no longer holds (session ended, expired, an allow-listed scope lost), nothing is written.
- [x] The superseded refresh token is revoked at Biatec OIDC when a revocation endpoint is configured, otherwise parked encrypted in PendingRevocation; a failure to do so does not undo the elevation.
- [x] Existing elevation and vault tests stay green.

## Verification

`dotnet test backend --filter "Elevation|Vault|Auth"` (the Redis tests run when Redis is reachable), full suite in CI.

## Hand-off

- **Summary:** `ITokenVault.ReplaceAsync` (the Redis `TokenVault` takes the refresh lock, reads the entry inside it, writes only while still holding it; in-memory fakes get a default read-and-write). `ElevationCompletion` replaces the entry through it, re-running the expiry and held-scope checks on the entry read inside the lock, then revokes or parks the superseded refresh token (best effort, never undoes the elevation). docs/security-notes-logout-and-revocation.md has a section on it.
- **Verification:** Elevation + Vault tests: 46 pass (Redis reachable), including a refresh in progress that the replacement waits for and then replaces, a declined replacement, and the superseded token parked after an elevation. Full suite in CI.

## Review

- 2026-10-23 13:31 · approve · PR #333 merged into main @ 7d8aa17 (recorded by kanban-sync)

## Activity log

- 2026-10-23 13:20 · kanban-split · (new) → backlog · created from audit issue #289
- 2026-10-23 13:20 · scholtz · taken · status stays backlog
- 2026-10-23 13:20 · session-20261023-1320 · backlog → in-progress · claimed, branch task/T-207
- 2026-10-23 13:31 · user · in-progress → done · PR #333 merged into main @ 7d8aa17 (synced by kanban-sync)
