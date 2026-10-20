---
id: T-095
title: "Fix the red widget i18n test and the flaky session-profile test on main"
status: in-progress
priority: P1
epic: "Deploy & docs"
blocked_by: []
area: ["packages/chat-widget/src/components/chat/ChatView.vue", "packages/chat-widget/test/chat-conversation.test.ts", "packages/chat-widget/test/dex-integration.test.ts", "backend/Biatec.Chat.Tests/Sessions/SessionProfileTests.cs"]
parallel: no
conflicts_with: [T-092, T-094]
issue: "#190"
branch: "task/T-095"
owner: "session-20261020-claude"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-095 · Fix the red widget i18n test and the flaky session-profile test on main

## Context

Found while verifying T-086 and T-094 on main (2026-10-20). (1) `pnpm -C packages/chat-widget test` fails on main: `chat-conversation.test.ts > i18n > has no string literal in the template outside the catalog` flags `emit('authorizePreview', …)` in ChatView.vue (the event name is a code identifier, not a user-visible string). (2) `test/dex-integration.test.ts` fails to load on a fresh checkout until the widget is built, because playground/dex imports `@biatec/chat-widget` from dist. (3) `SessionProfileTests.Fill_IsSingleFlightedPerSession` is timing-based (`Task.Delay(50)` then counts MCP calls) and failed once under a loaded full run (passes alone).

## Goal

The widget and backend test suites are green on a fresh checkout, without timing races.

## Scope

- In: fix the i18n test (or ChatView) so code identifiers in emit() calls aren't flagged while real user-visible literals still are; make dex-integration resolve the widget from source in tests (or build it in the test setup) so it runs on a fresh checkout; make Fill_IsSingleFlightedPerSession deterministic (wait on a signal / poll with a deadline instead of a fixed sleep).
- Out: feature changes.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-092, T-094: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] `pnpm -C packages/chat-widget test` passes on a fresh checkout (no prior build)
- [ ] The i18n test still fails on a real user-visible string literal (test)
- [ ] Fill_IsSingleFlightedPerSession has no fixed sleep and passes 20/20

## Verification

Fresh worktree: `pnpm install && pnpm -C packages/chat-widget test`; `dotnet test --filter SessionProfileTests` 20 times.

## Hand-off

## Review

## Activity log

- 2026-10-20 05:31 · coordinator · (new) → backlog · proposed by a worker / found during verification
- 2026-10-20 05:31 · coordinator · backlog · conflicts_with + T-092, T-094 (blockers scan: shared files)
- 2026-10-20 05:45 · scholtz · backlog → in-progress · claimed with session-20261020-claude on branch task/T-095 (main CI is red on the i18n test, which blocks the stage deploy)
