---
id: T-137
title: "Repair the playground alias so the widget suite passes on Windows too (BCHAT-045)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/vite.config.ts"]
parallel: yes
conflicts_with: []
issue: "#298"
branch: "task/T-137"
owner: "session-20261023-1156"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-137 - Repair the playground alias so the widget suite passes on Windows too (BCHAT-045)

## Context

Security audit finding BCHAT-045 (Low, issue #298): the widget test suite has five failures in the DEX playground tests
(`Failed to resolve import "@/i18n" from "playground/dex/DexApp.vue"`). A failing suite masks regressions. The alias in `vite.config.ts` is a
string key `"@/"` mapped to `fileURLToPath(new URL("./playground/dex/host/", ...))`, which has backslashes on Windows; CI (Linux) passes.

## Goal

The widget suite passes on Windows and Linux.

## Scope

- In: the test alias in `packages/chat-widget/vite.config.ts`, plus two other Windows/current-Node test-environment causes found once it worked (CRLF in the DEX doc test; the Node 25+ localStorage global).
- Out: the playground build config (`playground/vite.config.ts`).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes this file.

## Acceptance criteria

- [x] `pnpm --filter @biatec/chat-widget test` shows no failures on Windows.
- [x] CI's widget job still passes on Linux.

## Verification

`pnpm --filter @biatec/chat-widget test`; the PR's CI run.

## Hand-off

- **Summary:** Three environment-dependent causes fixed so the widget suite passes on a Windows checkout with a current Node: the playground alias is a regex alias with a forward-slash target (the string key with a backslash path did not resolve); the DEX doc test normalises CRLF before matching its code fences; a test setup file puts an in-memory Storage in place when the Node 25+ built-in localStorage global hides jsdom's (a no-op on Node 22, which CI uses).
- **Files changed:** packages/chat-widget/vite.config.ts, test/setup.ts, test/dex-integration.test.ts.
- **How it was verified:** pnpm --filter @biatec/chat-widget test on Windows with Node 26: 27 files, 413 passed, 5 skipped, 0 failed (was 5 failed); the PR's CI run (Linux, Node 22) succeeded.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The issue named only the alias; the other two causes (CRLF checkout, Node 25+ localStorage) turned up once the alias worked. None is a product defect. Other tests read repository files and could break on a CRLF checkout too; a .gitattributes with eol=lf would fix that at the source but renormalises the tree, so I left it. The storage shim is an in-memory Storage, not jsdom's (property-style access like localStorage.foo is not supported).

## Review

## Activity log

- 2026-10-23 19:00 · claude · (new) → backlog · created from GitHub issue #298 (BCHAT-045); blockers scan: none
- 2026-10-23 11:56 · scholtz · taken, and session-20261023-1156 · backlog → in-progress · claimed
