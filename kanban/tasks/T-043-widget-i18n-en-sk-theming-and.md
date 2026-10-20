---
id: T-043
title: Widget Slovak translation and accessibility pass
status: done
priority: P2
epic: "Widget"
blocked_by: [T-042, T-064, T-066]
area: ["packages/chat-widget/src/i18n/**", "packages/chat-widget/src/styles/**"]
parallel: no
conflicts_with: [T-036, T-077]
issue: "#44"
branch: "task/T-043"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#accessibility-and-localization"
created: 2026-10-19
updated: 2026-10-20
---

# T-043 · Widget Slovak translation and accessibility pass

## Context

WCAG 2.1 AA, vue-i18n with English and Slovak, theme auto, prefers-reduced-motion.

Widget UI rework: the vue-i18n setup, English catalog, Biatec AI tokens, light/dark/host theming and reduced-motion handling now come from T-062, and each UI task adds its English strings. This task adds the Slovak catalog, the host-locale fallback (any other locale → English, `setLocale` follows the host) and audits accessibility across the reworked UI (T-062 to T-066, T-042) in both themes, against the rules in `packages/chat-widget/AGENTS.md`.

## Goal

Launch-quality widget polish.

## Scope

- In: Slovak catalog for every string, locale fallback, contrast checks in dark and light, 44 px touch targets on mobile, axe test over every view (empty, conversation, preview, review dialog, permissions dialog, activity), reduced-motion audit.
- Out: token and theme implementation (T-062).
- Out: New features.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036, T-077: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] All UI strings come from catalogs in en and sk; an unsupported locale (de) falls back to English
- [ ] axe passes in both `data-theme="dark"` and `data-theme="light""
- [ ] axe reports no serious violations
- [ ] Reduced motion disables animations

## Verification

pnpm --filter chat-widget test

## Hand-off

## Review

- 2026-10-20 04:34 · approve · PR #181 merged into main @ 4ac6a9b (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 22:40 · coordinator · blocked · widget UI rework: theming moved to T-062; now Slovak catalog + a11y audit of the reworked UI, blocked_by T-064, T-066
- 2026-10-20 03:20 · coordinator · blocked · added T-077 to conflicts_with (new task)
- 2026-10-20 02:18 · kanban-board · blocked → backlog · blockers done: T-042, T-064, T-066
- 2026-10-20 04:05 · scholtz · taken · status stays backlog
- 2026-10-20 04:05 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-20 04:34 · user · in-progress → done · PR #181 merged into main @ 4ac6a9b (synced by kanban-sync)
