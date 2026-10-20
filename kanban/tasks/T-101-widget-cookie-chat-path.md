---
id: T-101
title: "Bug: widget cookie transport must POST /chat/messages"
status: done
priority: P0
epic: "Widget"
blocked_by: []
area: ["packages/chat-widget/src/transport/**", "packages/chat-widget/test/transport.test.ts", "packages/chat-widget/test/paths.test.ts"]
parallel: yes
conflicts_with: []
issue: "#207"
branch: "task/T-101"
owner: "coordinator-session"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#chat-widget-biatecchat-widget"
created: 2026-10-20
updated: 2026-10-20
---

# T-101 · Bug: widget cookie transport must POST /chat/messages

## Context

Found by the T-100 worker against a local backend. `PATHS.cookieChat` in `packages/chat-widget/src/transport/types.ts`
is `/chat`, but the backend serves only `POST /chat/messages` (`backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs:28`), so on
every host where the session cookie works (stage demo, web, DEX) the first chat message gets a 404. The transport test
asserted the same wrong path.

## Goal

Signed-in (cookie transport) chat reaches the backend, and a test pins every widget path to the backend's routes.

## Scope

- In: fix the path; a test that every `PATHS` entry is a route in the backend OpenAPI document (chat-protocol).
- Out: anything else.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] The cookie transport posts to `/chat/messages` (test)
- [ ] A test fails when a widget path isn't a backend route (verified against the old `/chat`)

## Verification

`pnpm -C packages/chat-widget test`.

## Hand-off

## Review

- 2026-10-20 07:41 · approve · PR #208 merged into main @ 177bdbb (recorded by kanban-sync)

## Activity log

- 2026-10-20 07:40 · coordinator · (new) → in-progress · P0 found by the T-100 worker; fixed directly by the coordinator
- 2026-10-20 07:41 · user · in-progress → done · PR #208 merged into main @ 177bdbb (synced by kanban-sync)
