---
id: T-001
title: Scaffold monorepo and backend solution
status: backlog
priority: P0
epic: "Foundation"
blocked_by: []
area: ["backend/*.slnx", "backend/*/*.csproj", "package.json", "pnpm-workspace.yaml", ".gitignore", ".editorconfig", "README.md", "docker-compose.yml"]
issue: "#2"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#repository-layout"
created: 2026-10-19
updated: 2026-10-19
---

# T-001 · Scaffold monorepo and backend solution

## Context

Repository layout: packages/{chat-widget,chat-protocol,consent-protocol}, backend/{Biatec.Chat.Api,Core,Tools,Tests}, apps/biatec_authenticator, docs/, k8s/.

## Goal

Empty but building skeleton so later tasks never touch shared root files.

## Scope

- In: Folders per layout; backend/Biatec.Chat.slnx with 4 net10.0 projects and references (Api->Core,Tools; Tools->Core; Tests->all); pnpm workspace listing packages/*, packages/consent-protocol/*, builders and e2e; root .gitignore/.editorconfig/README; docker-compose.yml with redis:7 and postgres:16 services (named volumes) plus a commented placeholder for the builder sidecar.
- Out: Any feature code.

## Acceptance criteria

- [ ] dotnet build backend/Biatec.Chat.slnx succeeds
- [ ] dotnet test runs one placeholder test green
- [ ] pnpm install succeeds at root
- [ ] docker compose up -d starts redis and postgres

## Verification

dotnet build; dotnet test; pnpm install; docker compose up -d && docker compose ps

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
