---
id: T-206
title: "A global request body limit; bound the load an unauthenticated /health can cause (BCHAT-035)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Security/RequestLimits.cs", "backend/Biatec.Chat.Api/Health/**", "backend/Biatec.Chat.Api/Program.cs", "backend/Biatec.Chat.Tests/Security/**", "backend/Biatec.Chat.Tests/ConfigAndHealthTests.cs", "docs/threat-model.md"]
parallel: yes
conflicts_with: []
issue: "#288"
branch: "task/T-206"
owner: "session-20261023-1306"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-206 - A global request body limit; bound the load an unauthenticated /health can cause (BCHAT-035)

## Context

Security audit finding BCHAT-035 (Low, issue #288, IDA-3, RISK-021). Only `/onboarding/**` has a request body limit (32 KiB); every other route accepts Kestrel's default 30 MB. `/health` is reachable through the public ingress (`path: /`), needs no authentication, and every call opens a PostgreSQL connection and pings Redis, so a caller can generate dependency load at will.

## Goal

Every request body is limited (64 KiB by default, `Limits:MaxRequestBodyBytes`), and `/health` costs at most one dependency check per few seconds however often it is called, and is rate limited per remote IP.

## Scope

- In: a global body-limit middleware (Content-Length and chunked), a cached `/health` (`Limits:HealthCacheSeconds`, default 5) with a per-IP rate limit; tests; a note in docs/threat-model.md.
- Out: ingress changes (kubelet probes use the same path, `/health/live` stays dependency free).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] A request with a body over the limit gets 413 before any handler, with a declared length and with a chunked body.
- [x] Existing routes with legitimate bodies (chat message, onboarding, operator KB page of 16 KiB) still work.
- [x] Repeated `/health` calls inside the cache window run the dependency checks once; the answer and the 200/503 mapping are unchanged.
- [x] `/health` is rate limited per remote IP and answers 429 beyond the limit.

## Verification

`dotnet test backend --filter "Health|RequestLimits|Onboarding|Admin"`, full suite in CI.

## Hand-off

- **Summary:** `Security/RequestLimits.cs`: `UseRequestBodyLimit` (413 on a declared length over `Limits:MaxRequestBodyBytes`, default 64 KiB, and the server's `IHttpMaxRequestBodySizeFeature` lowered for chunked bodies; stricter route limits stay) and `HealthResultCache` / `CachedHealthCheck`: the Redis and PostgreSQL checks run at most every `Limits:HealthCacheSeconds` each, one at a time, and a failing or throwing check is cached too. `/health` stays the framework's `MapHealthChecks` (stock status mapping, no-cache headers, HEAD) with a per-IP rate limit (`Limits:HealthRateLimitPermits`, default 120/min). A row in docs/threat-model.md.
- **Verification:** RequestLimitsTests + ConfigAndHealth: 25 pass (cache window, concurrent callers, failing check, headers/HEAD, rate limit, body limit); Onboarding/Admin/Kb/Public: 230 pass. Full suite in CI.

## Review

- 2026-10-23 13:19 · approve · PR #332 merged into main @ ed6bec9 (recorded by kanban-sync)

## Activity log

- 2026-10-23 13:06 · kanban-split · (new) → backlog · created from audit issue #288
- 2026-10-23 13:06 · scholtz · taken · status stays backlog
- 2026-10-23 13:06 · session-20261023-1306 · backlog → in-progress · claimed, branch task/T-206
- 2026-10-23 13:19 · user · in-progress → done · PR #332 merged into main @ ed6bec9 (synced by kanban-sync)
