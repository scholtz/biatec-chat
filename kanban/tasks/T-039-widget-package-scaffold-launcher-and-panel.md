---
id: T-039
title: Widget package scaffold, launcher and panel
status: done
priority: P0
epic: "Widget"
blocked_by: [T-001]
area: ["packages/chat-widget/**"]
parallel: no
conflicts_with: [T-036]
issue: "#40"
branch: "task/T-039"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#chat-widget-biatecchat-widget"
created: 2026-10-19
updated: 2026-10-19
---

# T-039 · Widget package scaffold, launcher and panel

## Context

@biatec/chat-widget: Vue 3 peer dep, ESM+UMD, one plugin call with backendUrl, clientKey, locale, theme, suggestions; styles scoped under biatec-chat with CSS variables; round launcher bottom-right; drawer on desktop, full-screen on mobile.

## Goal

Installable widget shell.

## Scope

- In: Vite library build, plugin install API (including the opt-in host page context string; nothing read from the host page by default), launcher, panel with header (tier display placeholder), scoped CSS tokens, playground app (packages/chat-widget/playground).
- Out: Transport and chat logic (T-040).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] pnpm --filter chat-widget build emits ESM, UMD and style.css
- [ ] Playground shows launcher and opens panel
- [ ] Styles don't leak outside the root
- [ ] Keyboard accessible launcher

## Verification

pnpm --filter chat-widget build; pnpm --filter chat-widget dev

## Hand-off

## Review

- 2026-10-19 20:06 · approve · PR #82 merged into main @ 762bb6b (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added opt-in page context option from Packaging section
- 2026-10-19 18:21 · kanban-board · blocked → backlog · blockers done: T-001
- 2026-10-19 19:42 · scholtz · taken · status stays backlog
- 2026-10-19 19:42 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 20:06 · user · in-progress → done · PR #82 merged into main @ 762bb6b (synced by kanban-sync)
