---
id: T-043
title: Widget i18n (en, sk), theming and accessibility pass
status: blocked
priority: P2
epic: "Widget"
blocked_by: [T-042]
area: ["packages/chat-widget/src/i18n/**", "packages/chat-widget/src/styles/**"]
parallel: no
conflicts_with: [T-036]
issue: "#44"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#accessibility-and-localization"
created: 2026-10-19
updated: 2026-10-19
---

# T-043 · Widget i18n (en, sk), theming and accessibility pass

## Context

WCAG 2.1 AA, vue-i18n with English and Slovak, theme auto, prefers-reduced-motion.

## Goal

Launch-quality widget polish.

## Scope

- In: Message catalogs, locale option, dark/light tokens, reduced motion, contrast checks, axe test.
- Out: New features.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] All UI strings come from catalogs in en and sk
- [ ] Theme follows prefers-color-scheme with auto
- [ ] axe reports no serious violations
- [ ] Reduced motion disables animations

## Verification

pnpm --filter chat-widget test

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
