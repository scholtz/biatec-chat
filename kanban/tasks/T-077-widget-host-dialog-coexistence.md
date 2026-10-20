---
id: T-077
title: Widget fixes for coexisting with host dialogs (Esc, focus trap, lang, chip export)
status: backlog
priority: P2
epic: "Widget"
blocked_by: [T-063]
area: ["packages/chat-widget/src/components/BiatecChatRoot.vue", "packages/chat-widget/src/index.ts", "packages/chat-widget/scripts/emit-types.mjs"]
parallel: no
conflicts_with: [T-043]
issue: "#144"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#accessibility-and-localization"
created: 2026-10-20
updated: 2026-10-20
---

# T-077 · Widget fixes for coexisting with host dialogs (Esc, focus trap, lang, chip export)

## Context

Found by the T-068 worker while running the widget inside the BiatecDEX-like playground (`playground/dex.html`):

- Esc pressed inside a host PrimeVue Dialog also closes the floating Biatec AI panel: the widget's document keydown
  handler doesn't know the key was meant for the dialog.
- At 375 px and in the workspace view, the widget's full-screen Tab trap moves focus out of a host dialog opened on
  top of it.
- The root `lang` is set to the requested locale (e.g. `de`) while the text falls back to English, so screen
  readers use the wrong pronunciation.
- The design wants the Biatec AI chip next to "Ask Biatec AI" links on host pages; the chip isn't exported, so the
  playground uses `pi pi-comments`.

## Goal

The widget never steals Esc or focus from a host dialog, announces the language it actually shows, and hosts can use
the Biatec AI chip.

## Scope

- In: ignore Esc that was handled by, or originated in, a host dialog (check `defaultPrevented` and whether the event
  target is inside the widget or a host `[role=dialog][aria-modal=true]`); suspend the full-screen Tab trap while a
  host modal dialog is open; set the root `lang` to the locale the catalog actually resolved; export the 16 px
  Biatec AI symbol component and its typings.
- Out: Slovak strings and the accessibility pass (T-043).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-043: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Esc inside a host dialog closes only the dialog (test with a host `role=dialog` element, and in the DEX playground)
- [ ] With a host modal dialog open over a full-screen panel, Tab stays in the dialog
- [ ] `lang` on the widget root equals the resolved catalog locale (de → en)
- [ ] The chip is exported and appears in `dist/index.d.ts`

## Verification

`pnpm -C packages/chat-widget typecheck`, `test`, `build`; the DEX playground at 375 px and 1280 px.

## Hand-off

## Review

## Activity log

- 2026-10-20 03:20 · coordinator · (new) → backlog · proposed by T-068; conflicts with T-043 (lang / i18n resolution)
