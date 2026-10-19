---
id: T-027
title: Node builder sidecar for CLAMM and algo-safe
status: blocked
priority: P0
epic: "DEX"
blocked_by: [T-001]
area: ["builders/**", "docker-compose.yml", "backend/Biatec.Chat.Tools/Builders/**"]
branch: ""
owner: ""
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

## Acceptance criteria

- [ ] pnpm --filter builders test green
- [ ] Service rejects calls without the internal secret
- [ ] docker compose up starts it and /health is ok
- [ ] .NET client calls /health in a test

## Verification

pnpm --filter builders test; docker compose up builders

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
