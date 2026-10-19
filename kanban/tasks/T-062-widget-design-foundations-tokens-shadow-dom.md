---
id: T-062
title: Widget design foundations: Biatec AI tokens, Shadow DOM, theme sync, primitives
status: blocked
priority: P1
epic: "Widget UI rework"
blocked_by: [T-041]
area: ["packages/chat-widget/src/styles/**", "packages/chat-widget/src/styles.css", "packages/chat-widget/src/index.ts", "packages/chat-widget/src/options.ts", "packages/chat-widget/src/theme/**", "packages/chat-widget/src/ui/**", "packages/chat-widget/src/i18n/**", "packages/chat-widget/src/assets/**"]
parallel: yes
conflicts_with: []
issue: "#103"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#visual-design"
created: 2026-10-19
updated: 2026-10-19
---

# T-062 · Widget design foundations: Biatec AI tokens, Shadow DOM, theme sync, primitives

## Context

The widget is being reworked to the **Biatec AI** design system so it fits Biatec DEX (`scholtz/BiatecDEX`). Read `docs/design/README.md` (mapping and status table), `docs/design/biatec-ai-widget/README.md` (pixel spec), `packages/chat-widget/AGENTS.md` (UI rules), and the spec sections "Packaging" and "Visual Design".

Spec: "The widget renders into a Shadow DOM root attached to its own host element, so neither the host's CSS (PrimeVue themes, Tailwind preflight) nor the widget's CSS leaks across. Hosts restyle it only through CSS custom properties set on that host element." and "`theme: 'host'` follows the host's dark-mode class on `<html>` (`p-dark` by default …); `auto` follows `prefers-color-scheme`. Dark is the default look."

Today `src/styles.css` carries ad-hoc `--bc-*` tokens (blue primary, 12 px radius, system font) and the plugin mounts into a light-DOM `<div data-biatec-chat-host>`. Every later UI task (T-063, T-064, T-042, T-066) builds on what this task provides.

## Goal

The widget has the Biatec AI tokens, light/dark/host theming, Shadow DOM isolation, the logo and a small set of shared UI primitives, so feature tasks only compose them.

## Scope

- In: port `docs/design/biatec-ai-widget/tokens/biatec-tokens.css` (without the Google Fonts `@import`) into `src/styles/tokens.css`, scoped to the widget root, plus `--space-*`, `--radius-*`, motion and `--bc-z` (default 1000, below PrimeVue overlays); remove the old `--bc-*` colour tokens (keep `--bc-z`).
- In: render the widget into a shadow root on its host element; inject the compiled CSS into it (Vite `?inline` import); keep `@biatec/chat-widget/style.css` exported for hosts that mount `BiatecChatRoot` themselves.
- In: options `theme: 'auto' | 'light' | 'dark' | 'host'` and `hostDarkClass` (default `p-dark`); a `MutationObserver` on `<html>` for `host`; `data-theme` on the widget root.
- In: Biatec AI symbol as a Vue component (`bai-symbol.svg`, small variant ≤ 24 px, mono-dark variant for the launcher) from `docs/design/biatec-ai-widget/assets/`.
- In: `lucide-vue-next` dependency; primitives in `src/ui/`: Button (primary, secondary, ghost, danger, soft; 32/40/48), IconButton with tooltip on hover and focus, StatusBadge (the fixed status set: colour + glyph/icon + word), Chip, Dialog (focus trap, Esc, return focus, `aria-modal`), Toast (`aria-live=polite`), Spinner.
- In: `vue-i18n` instance with the English catalog and a `t()` the primitives use, so later tasks add strings to catalogs from the start (Slovak comes in T-043).
- Out: launcher, panel and header layout (T-063); messages and composer (T-064); preview cards (T-042); permissions and activity (T-066); Slovak strings and the axe audit (T-043).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] `pnpm --filter @biatec/chat-widget build` emits ESM, UMD and `style.css`; the bundle contains no `fonts.googleapis.com` URL
- [ ] The widget's DOM lives in a shadow root; a test page with Tailwind preflight and a global `button { background: red }` rule leaves the widget's buttons unchanged
- [ ] Setting `--color-brand-primary` on the host element restyles the primary button
- [ ] `theme: 'host'` switches `data-theme` within one frame of toggling `p-dark` on `<html>`; `auto` follows `prefers-color-scheme`; unset defaults to `auto` with dark tokens as the base
- [ ] Every primitive has a test: Button sizes and disabled/loading (`aria-busy`), IconButton requires `aria-label`, Dialog traps focus and returns it on Esc, StatusBadge renders word + icon for every row of the status mapping in `docs/design/README.md`
- [ ] No hex or px literal in component styles that a token carries (grep check in the test suite)
- [ ] Existing tests still pass (`styles.test.ts` updated for the new tokens and shadow root)

## Implementation notes

- Custom properties inherit into the shadow root, so a host override on the host element works; declare defaults on `:host` and the theme overrides on `:host([data-theme=light])` (or a root element inside the shadow root carrying `data-theme`).
- BiatecDEX already defines `--radius-lg` and `--shadow-lg`; inside the shadow root the widget's own values win, which is intended.
- `--bc-z` 1000 keeps PrimeVue dialogs (auto z-index from 1100) above the floating widget; document it.
- Teleports from inside the widget must target the shadow root, not `document.body`.
- jsdom supports `attachShadow`; query through `host.shadowRoot` in tests.
- Split the English catalog per feature (`src/i18n/en/ui.json` here; `shell.json` T-063, `chat.json` T-064, `cards.json` T-042, `agent.json` T-066, `data.json` T-067/T-069) and merge them in `src/i18n/index.ts`, so parallel UI tasks never edit the same catalog file. T-043 adds `src/i18n/sk/*.json`.

## Verification

pnpm --filter @biatec/chat-widget test; pnpm --filter @biatec/chat-widget typecheck; pnpm --filter @biatec/chat-widget build && ! grep -q fonts.googleapis packages/chat-widget/dist/*.js; manual: playground with `theme: 'host'`, toggle `p-dark` in devtools

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
