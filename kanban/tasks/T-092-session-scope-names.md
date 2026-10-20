---
id: T-092
title: "Session endpoint: return the session's OIDC scope names"
status: backlog
priority: P3
epic: "Sessions & auth"
blocked_by: [T-083]
area: ["backend/Biatec.Chat.Api/Sessions/SessionEndpoints.cs", "backend/Biatec.Chat.Tests/Sessions/**"]
parallel: yes
conflicts_with: []
issue: "#179"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-092 · Session endpoint: return the session's OIDC scope names

## Context

Proposed by the T-083 worker. `SessionEndpoints.cs` still returns `Scopes = []` although `TokenVaultEntry.Scopes` holds the scope names and the session contract documents them; the widget will need them to show "Authorize signing" only when `sign` is missing.

## Goal

GET /session returns the scope names the session holds (never tokens).

## Scope

- In: read the scope names with the cached session profile from T-083 (no extra vault decrypt per poll); guests get an empty list.
- Out: widget changes.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] A signed-in session returns its scope names; a guest returns []
- [ ] No extra vault read per poll (test)

## Verification

`dotnet test --filter Session`, then the full backend suite.

## Hand-off

## Review

## Activity log

- 2026-10-20 04:16 · coordinator · (new) → backlog · proposed by a worker
