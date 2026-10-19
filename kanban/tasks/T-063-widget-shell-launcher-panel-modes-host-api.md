---
id: T-063
title: Widget shell: launcher, panel modes, header and host API
status: backlog
priority: P1
epic: "Widget UI rework"
blocked_by: [T-062]
area: ["packages/chat-widget/src/components/ChatLauncher.vue", "packages/chat-widget/src/components/ChatPanel.vue", "packages/chat-widget/src/components/BiatecChatRoot.vue", "packages/chat-widget/src/components/shell/**", "packages/chat-widget/src/api/**"]
parallel: no
conflicts_with: [T-064]
issue: "#104"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-19
updated: 2026-10-20
---

# T-063 · Widget shell: launcher, panel modes, header and host API

## Context

Read `docs/design/README.md` (mapping and status table), `docs/design/biatec-ai-widget/README.md` (pixel spec), `packages/chat-widget/AGENTS.md` (UI rules); prototype screens 1 to 3 (launcher, minimized bar, chat panel) in `docs/design/biatec-ai-widget/README.md`.

Spec, User Experience 1 and 2: "**Launcher**: a 60 px brand-green button with the Biatec AI chip in the bottom-right corner (24 px from the edges, positionable by the host), with a tooltip "Ask Biatec AI" and an unread badge … A minimized panel collapses to a small bar with the status line and a Restore button." "**Panel**: three modes. **Floating** (default) is a 420 × 700 px panel above the launcher; **inline** fills a host column …; **workspace** is full screen with the Activity aside next to the conversation. Below 640 px an open panel is always full screen. The header shows the Biatec AI chip, the current tier ("Guest", or the signed-in email with the active primary address), a sign-in or sign-out control, and expand, minimize and close buttons. Under it, a **status pill** shows the agent mode … and opens the permissions dialog; an **Activity** toggle opens the session timeline."

Packaging: "A host can drive the widget from its own UI through `useBiatecChat()`: `open()`, `close()`, `ask(prompt)` …, `setPageContext(context)`, `setTheme(theme)` and `setLocale(locale)`." BiatecDEX uses these (see `docs/integration-biatec-dex.md`).

## Goal

The launcher, minimized bar, panel frame in floating, inline and workspace modes, header and status bar match the design, and a host can open the widget and send a prompt from its own buttons.

## Scope

- In: launcher (60 × 60, radius 8, chip 44 px mono-dark, tooltip, unread badge cleared on open, focus moves to the composer on open); minimized bar; panel geometry per mode; full screen below 640 px; transitions 220 ms with reduced-motion fallback.
- In: header (chip 42, "Biatec AI", subtitle with tier: "Guest" or email · short primary address; Sign in / Sign out using the sign-in flow from T-041; Expand/Exit full screen, Minimize (floating only), Close icon buttons).
- In: status bar: status pill with dot + "Status:" + mode label + chevron (emits `open-permissions`; shows "Analyze only" for Guest, reads the agent mode from session state when T-065 has landed, otherwise "Prepare for approval" when signed in) and an Activity toggle with `aria-pressed` (emits `toggle-activity`). Slots or regions where T-066 mounts the dialog and the aside.
- In: option `mode: 'floating' | 'inline' | 'workspace'`; exported `BiatecChatInline` component; only one instance renders, the launcher hides while an inline panel is mounted.
- In: `useBiatecChat()` with `open`, `close`, `ask`, `setPageContext`, `setTheme`, `setLocale`; `ask` opens the panel and sends the prompt through the chat composable as a normal, visible user message.
- Out: message list, empty state and composer styling (T-064); permissions dialog and activity aside contents (T-066); preview cards (T-042).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-064: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Playground shows launcher → panel → minimized bar → restore → workspace → exit, matching the design geometry (component test asserts classes/styles per mode)
- [ ] At a 375 px viewport an open panel is full screen; at 1280 px floating is 420 px wide
- [ ] Header shows "Guest" before sign-in and the email and short primary address after (fixture session)
- [ ] Status pill and Activity toggle are buttons with `aria-expanded` / `aria-pressed`; all icon buttons have `aria-label` and a tooltip on focus
- [ ] `useBiatecChat().ask('Swap 50 USDC to ALGO')` opens the panel and the prompt appears as a user message and is sent once; calling it twice quickly while a reply streams queues or refuses visibly, never sends hidden text
- [ ] `setTheme`, `setLocale` and `setPageContext` take effect without remount; the page context is sent only after the panel has been opened
- [ ] Inline mode: `BiatecChatInline` renders in a `minmax(360px, 420px)` column without shadow, and the floating launcher hides

## Implementation notes

- Keep the transport untouched: opening the panel still triggers the cookie probe (T-040), so `ask` before the first open must open first.
- Esc order in the panel: open dialog → aside sheet → nothing (Esc does not close the panel in floating mode while the composer has text).
- Unread badge: increments when an assistant message completes while the panel is closed or minimized.

## Verification

pnpm --filter @biatec/chat-widget test; pnpm --filter @biatec/chat-widget dev and walk the modes at 375 px and 1280 px; screenshots attached to the hand-off

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-064: ask() goes through the chat composable in components/chat/**
- 2026-10-20 00:55 · kanban-board · blocked → backlog · blockers done: T-062
