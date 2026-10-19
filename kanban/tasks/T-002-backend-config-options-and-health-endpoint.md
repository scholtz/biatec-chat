---
id: T-002
title: Backend config, options and health endpoint
status: blocked
priority: P0
epic: "Foundation"
blocked_by: [T-001]
area: ["backend/Biatec.Chat.Api/Program.cs", "backend/Biatec.Chat.Api/Config/**", "backend/Biatec.Chat.Api/appsettings*.json", "backend/Biatec.Chat.Api/Health/**"]
issue: "#3"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#chat-backend-biatecchatapi"
created: 2026-10-19
updated: 2026-10-19
---

# T-002 · Backend config, options and health endpoint

## Context

Backend is stateless except Redis and PostgreSQL; secrets come from k8s secrets or AWS Secrets Manager, never appsettings.

## Goal

Typed options and a /health endpoint the rest of the backend builds on.

## Scope

- In: Options classes bound from config: Oidc (issuer, mcpResource), Mcp (endpoint), Bedrock (region eu-central-1, anon/auth model ids, maxTokens), Redis, Postgres, Algod per network, Kms. Validation on start. /health with Redis+Postgres checks. Secrets only via env/user-secrets.
- Out: Feature endpoints.

## Acceptance criteria

- [ ] App starts with appsettings.Development.json against compose services
- [ ] /health returns Healthy with both deps up, Unhealthy when redis is stopped
- [ ] Missing required option fails startup with a clear message
- [ ] No secret values in committed appsettings

## Verification

dotnet run; curl localhost:<port>/health; stop redis and curl again

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
