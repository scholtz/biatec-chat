---
id: T-027
title: Node builder sidecar for CLAMM and algo-safe
status: done
priority: P0
epic: "DEX"
blocked_by: [T-001]
area: ["builders/**", "docker-compose.yml", "backend/Biatec.Chat.Tools/Builders/**"]
parallel: no
conflicts_with: [T-057]
issue: "#28"
branch: "task/T-027"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#tool-executors"
created: 2026-10-19
updated: 2026-10-19
---

# T-027 · Node builder sidecar for CLAMM and algo-safe

## Context

Liquidity, pool swaps and safes must use the npm libraries biatec-concentrated-liquidity-amm and algo-safe; the backend is .NET, so an internal Node service builds those groups.

## Goal

Internal HTTP service the .NET Tools call to build unsigned groups.

## Scope

- In: builders/ Node 22 TypeScript service (Hono), internal shared-secret auth, health, JSON in/out of base64 unsigned transactions, network config (algod per network), Dockerfile, compose service (uncomment placeholder), .NET typed client in backend/Biatec.Chat.Tools/Builders.
- Out: Specific builders (T-028, T-030).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-057: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] pnpm --filter builders test green
- [ ] Service rejects calls without the internal secret
- [ ] docker compose up starts it and /health is ok
- [ ] .NET client calls /health in a test

## Verification

pnpm --filter builders test; docker compose up builders

## Hand-off

## Review

- 2026-10-19 20:10 · approve · PR #83 merged into main @ afda417 (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:21 · kanban-board · blocked → backlog · blockers done: T-001
- 2026-10-19 19:42 · scholtz · taken · status stays backlog
- 2026-10-19 19:42 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 20:10 · user · in-progress → done · PR #83 merged into main @ afda417 (synced by kanban-sync)
