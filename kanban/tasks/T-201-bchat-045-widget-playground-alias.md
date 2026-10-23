---
id: T-201
title: "Repair the playground alias so the DEX playground tests run (BCHAT-045)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/vite.config.ts", "packages/chat-widget/test/dex-*.test.ts"]
parallel: yes
conflicts_with: []
issue: "#298"
branch: "task/T-201"
owner: "session-20261023-1208"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-201 - Repair the playground alias so the DEX playground tests run (BCHAT-045)

## Context

Security audit finding BCHAT-045 (Low, issue #298, TEST-002). `test/dex-host-a11y.test.ts` and `test/dex-integration.test.ts` fail with `Failed to resolve import "@/i18n" from "playground/dex/DexApp.vue"`. The vitest alias `"@/"` is a string key, and Vite's alias matching only accepts a string key when the next character in the import is `/` (`@//i18n`), so `@/i18n` never matches. A failing suite masks regressions, which is why this is recorded.

## Goal

The widget test suite has no baseline failures: the DEX playground tests resolve the host modules and run.

## Scope

- In: the test alias in the widget's vite config; any test the now-running suites expose as broken because of the stale baseline.
- Out: the playground itself.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `pnpm --filter @biatec/chat-widget test` passes with no failing file.
- [ ] The `@/` alias resolves host modules in vitest on Windows and Linux paths.

## Verification

`pnpm --filter @biatec/chat-widget test`, `pnpm --filter @biatec/chat-widget typecheck`.

## Hand-off

## Review

## Activity log

- 2026-10-23 12:08 · kanban-split · (new) → backlog · created from audit issue #298
- 2026-10-23 12:08 · scholtz · taken · status stays backlog
- 2026-10-23 12:08 · session-20261023-1208 · backlog → in-progress · claimed, branch task/T-201
