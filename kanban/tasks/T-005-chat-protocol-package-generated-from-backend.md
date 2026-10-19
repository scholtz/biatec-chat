---
id: T-005
title: chat-protocol package generated from backend OpenAPI
status: done
priority: P0
epic: "Foundation"
blocked_by: [T-001]
area: ["packages/chat-protocol/**", "backend/Biatec.Chat.Api/Contracts/**"]
parallel: yes
conflicts_with: []
issue: "#6"
branch: "task/T-005"
owner: ""
source: "PRODUCT-DESCRIPTION.md#products-in-this-monorepo"
created: 2026-10-19
updated: 2026-10-19
---

# T-005 · chat-protocol package generated from backend OpenAPI

## Context

packages/chat-protocol: typed JSON contract between widget and backend (messages, suggestions, action previews, consent status), generated from the backend OpenAPI document.

## Goal

Contract DTOs and a generation script so widget and backend share types.

## Scope

- In: Contracts in backend: session state, chat message request, SSE event union (text delta, tool status, preview card, consent status, sign-in card, error), action preview + steps, suggestion. OpenAPI doc served in Development; script generating TS types into packages/chat-protocol; package build.
- Out: Endpoint implementations.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] OpenAPI document includes every contract
- [ ] pnpm --filter chat-protocol generate && build produces typed exports
- [ ] SSE event union is a discriminated union in TS
- [ ] README explains regeneration

## Verification

pnpm --filter chat-protocol generate; pnpm --filter chat-protocol build

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:21 · user · blocked → done · PR #65 merged into main on GitHub (board synced after the merge)
