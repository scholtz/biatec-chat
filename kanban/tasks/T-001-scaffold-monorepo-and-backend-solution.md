---
id: T-001
title: Scaffold monorepo and backend solution
status: done
priority: P0
epic: "Foundation"
blocked_by: []
area: ["backend/*.slnx", "backend/*/*.csproj", "package.json", "pnpm-workspace.yaml", ".gitignore", ".editorconfig", "README.md", "docker-compose.yml"]
parallel: yes
conflicts_with: []
issue: "#2"
branch: "task/T-001"
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

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] dotnet build backend/Biatec.Chat.slnx succeeds
- [x] dotnet test runs one placeholder test green
- [x] pnpm install succeeds at root
- [ ] docker compose up -d starts redis and postgres

## Verification

dotnet build; dotnet test; pnpm install; docker compose up -d && docker compose ps

## Hand-off

PR #63 (task/T-001). dotnet build/test and pnpm install verified; `docker compose config` valid but `docker compose up -d` NOT run (Docker daemon was off), so the last criterion is unchecked. Code review fixes: compose ports bound to 127.0.0.1, Postgres password overridable.

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → backlog · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 19:00 · session-20261019-1900 · backlog → in-progress · claimed via /kanban-next (GitHub issue #2)
- 2026-10-19 19:30 · user · in-progress → done · user explicitly asked in conversation to implement, review and merge; PR #63 merged into main
