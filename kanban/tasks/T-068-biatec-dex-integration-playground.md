---
id: T-068
title: BiatecDEX integration playground and hand-off doc
status: review
priority: P2
epic: "Widget UI rework"
blocked_by: [T-063]
area: ["packages/chat-widget/playground/**", "docs/integration-biatec-dex.md"]
parallel: yes
conflicts_with: []
issue: "#109"
branch: "task/T-068"
owner: "worker-T-068"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#visual-design"
created: 2026-10-19
updated: 2026-10-20
---

# T-068 · BiatecDEX integration playground and hand-off doc

## Context

The first host is Biatec DEX (`scholtz/BiatecDEX`: Vue 3, PrimeVue 5 Aura with `darkModeSelector: '.p-dark'`, Tailwind 4 with `tailwindcss-primeui`, `vue-i18n` with ten locales, `useTheme()` toggling `p-dark` on `<html>`). We never edit that repository (AGENTS.md); `docs/integration-biatec-dex.md` lists the host-side changes for its maintainers.

Spec, Visual Design: "**Fits Biatec DEX.** The tokens mirror Biatec DEX … The Biatec-specific host changes are in `docs/integration-biatec-dex.md`."

## Goal

This repo can show and test the widget inside a page that behaves like Biatec DEX, and the hand-off doc for BiatecDEX is accurate.

## Scope

- In: a second playground page `playground/dex.html` styled like BiatecDEX (PrimeVue Aura + Tailwind 4 + the DEX's `app.css` token values, `.p-dark` toggle, language switch en/sk/de, a pair header, an order card with "Ask Biatec AI about this order" calling `useBiatecChat().ask(...)`, a PrimeVue Dialog and Toast to check layering, and a wide layout using `BiatecChatInline`).
- In: tests (vitest + jsdom, or the existing `e2e/` Playwright setup if present) for theme following `p-dark`, locale fallback (de → en), page context being sent only after open, and z-index below a PrimeVue dialog.
- In: update `docs/integration-biatec-dex.md` to match the final API and remove its "planned" status for what is built.
- Out: changes in the BiatecDEX repository; publishing the npm package.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [x] `pnpm --filter @biatec/chat-widget dev` serves `/dex.html`; toggling dark/light and the language updates the widget live
- [x] "Ask Biatec AI about this order" opens the panel with the prompt and nothing executes without Confirm
- [x] A PrimeVue Dialog opened while the widget is open renders above it
- [x] Every code snippet in `docs/integration-biatec-dex.md` compiles against the built package (copied into the playground)

## Implementation notes

- PrimeVue 5 needs a licence key in production; the playground can use the unstyled/Aura mode without the banner check, or note the banner.
- Keep playground-only dependencies in `devDependencies`.

## Verification

pnpm --filter @biatec/chat-widget dev → open /dex.html; pnpm --filter @biatec/chat-widget test; screenshots (dark, light, mobile 375 px, inline) in the hand-off

## Hand-off

- **Summary:** `playground/dex.html` built like BiatecDEX (PrimeVue 5 Aura with `.p-dark`, Tailwind 4 + tailwindcss-primeui, the DEX token values, vue-i18n en/sk/de, theme and language switches, pair header, order card with "Ask Biatec AI about this order", PrimeVue Dialog and Toast, wide layout with `BiatecChatInline`), running the hand-off doc's snippets as written; `test/dex-integration.test.ts`; `docs/integration-biatec-dex.md` rewritten to the shipped API.
- **Branch:** `task/T-068` @ `350a16f` (merged with main), PR #140 (`Closes #109`).
- **Files outside the area:** widget `package.json` (6 devDependencies, `typecheck:playground` script), `pnpm-lock.yaml`, `test/dex-integration.test.ts`, a comment in `src/styles/tokens.css`.
- **Verification:** widget typecheck clean, tests 172/172, build OK, `typecheck:playground` OK, frozen-lockfile install OK; headless Chromium checks (dark/light, en/de, ask, dialog above the widget on desktop and at 375 px, inline, requests go to localhost:5000 with client key `dev`).
- **Review (medium):** fixed client key not accepted by the local dev tenant, `?theme=` persisted, empty backend URL falling through to production, wrong z-index comment, leaking test apps. Known limitations: the doc-snippet guard is line-substring based; `typecheck:playground` isn't in CI and needs a build first; the z-index check only scans `position: fixed` rules; unused host keys and helpers.
- **Open questions:** PrimeVue 5 shows an "Invalid PrimeUI License" banner over part of the launcher unless `PRIMEVUE_LICENSE` is set; doc step 1 uses `backendUrl: import.meta.env.VITE_BIATEC_CHAT_URL || 'https://chat.biatec.io'` (DEX maintainers to confirm).
- **Follow-up tasks created:** T-077 (widget fixes for coexisting with host dialogs).
- **Unblocks:** no task is blocked by T-068.

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-20 01:32 · kanban-board · blocked → backlog · blockers done: T-063
- 2026-10-20 01:40 · LukassSoukup · taken · status stays backlog
- 2026-10-20 01:40 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 02:09 · coordinator · in-progress → review · branch task/T-068 @ 350a16f, PR #140
