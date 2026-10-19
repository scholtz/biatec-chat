---
id: T-068
title: BiatecDEX integration playground and hand-off doc
status: blocked
priority: P2
epic: "Widget UI rework"
blocked_by: [T-063]
area: ["packages/chat-widget/playground/**", "docs/integration-biatec-dex.md"]
parallel: yes
conflicts_with: []
issue: "#109"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#visual-design"
created: 2026-10-19
updated: 2026-10-19
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

- [ ] `pnpm --filter @biatec/chat-widget dev` serves `/dex.html`; toggling dark/light and the language updates the widget live
- [ ] "Ask Biatec AI about this order" opens the panel with the prompt and nothing executes without Confirm
- [ ] A PrimeVue Dialog opened while the widget is open renders above it
- [ ] Every code snippet in `docs/integration-biatec-dex.md` compiles against the built package (copied into the playground)

## Implementation notes

- PrimeVue 5 needs a licence key in production; the playground can use the unstyled/Aura mode without the banner check, or note the banner.
- Keep playground-only dependencies in `devDependencies`.

## Verification

pnpm --filter @biatec/chat-widget dev → open /dex.html; pnpm --filter @biatec/chat-widget test; screenshots (dark, light, mobile 375 px, inline) in the hand-off

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
