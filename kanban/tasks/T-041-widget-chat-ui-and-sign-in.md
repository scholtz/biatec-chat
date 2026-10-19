---
id: T-041
title: Widget chat UI and sign-in popup
status: done
priority: P0
epic: "Widget"
blocked_by: [T-040]
area: ["packages/chat-widget/src/components/chat/**"]
parallel: no
conflicts_with: [T-036]
issue: "#42"
branch: "task/T-041"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-19
updated: 2026-10-19
---

# T-041 · Widget chat UI and sign-in popup

## Context

Streaming messages, compact tool status lines, sample prompts by tier, header shows Guest or email with primary address, sign-in via popup polled until complete, ARIA live region and focus trap.

## Goal

Usable chat for anonymous and signed-in users.

## Scope

- In: Message list, composer, suggestions, sign-in card, popup open and poll, header state, a11y basics.
- Out: Preview cards (T-042).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Streaming text renders progressively
- [ ] Sign-in popup completes and tier switches without reload
- [ ] Sample prompts depend on tier
- [ ] Screen reader announces new messages

## Verification

pnpm --filter chat-widget test; manual playground run against local backend

## Hand-off

## Review

- 2026-10-19 22:29 · approve · PR #99 merged into main @ cb0448d (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:19 · kanban-board · blocked → backlog · blockers done: T-040
- 2026-10-19 21:24 · scholtz · taken · status stays backlog
- 2026-10-19 21:24 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 22:29 · user · in-progress → done · PR #99 merged into main @ cb0448d (synced by kanban-sync)
