---
id: T-064
title: Widget conversation UI to the Biatec AI design
status: backlog
priority: P1
epic: "Widget UI rework"
blocked_by: [T-041, T-062]
area: ["packages/chat-widget/src/components/chat/**"]
parallel: no
conflicts_with: [T-063]
issue: "#105"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-19
updated: 2026-10-20
---

# T-064 · Widget conversation UI to the Biatec AI design

## Context

T-041 builds the working chat (streaming, tool status lines, sample prompts by tier, sign-in card, live region) with the old look. This task restyles it to the design without changing its behaviour. Read `docs/design/README.md` (mapping and status table), `docs/design/biatec-ai-widget/README.md` (pixel spec), `packages/chat-widget/AGENTS.md` (UI rules); prototype screens 3 (composer), 4 (empty state) and 5 (messages) in `docs/design/biatec-ai-widget/README.md`.

Spec: sample prompts shown on an empty session are chosen by tier and host context (User Experience 3); streaming replies with tool progress as compact status lines (4); copy voice "concrete, neutral and calm … no hype, no promised returns, no emoji" (Visual Design).

## Goal

An empty session, a streamed conversation, tool progress and the composer look and behave like the Biatec AI design.

## Scope

- In: empty state ("Meet Biatec AI.", intro text, 2 × 2 suggestion grid fed by the existing tier-based prompts with a Lucide icon each, a shield note stating the current agent mode).
- In: user bubble; assistant message with 28 px chip avatar, paragraphs, bullet lists (6 px brand square marker), follow-up chips from backend `Suggestion`s if present, action row Copy (→ "Copied") and Retry (resend the previous user message).
- In: thinking indicator (three pulsing squares + "Analyzing…", `role=status`, static under reduced motion); tool status lines restyled as compact secondary rows with an icon.
- In: composer: textarea 2 rows (min 52, max 140 px, auto-grow), placeholder "Ask Biatec AI about Biatec, Algorand or your wallet…", Enter sends / Shift+Enter newline / ignore during IME, context chip showing the host page context when set (toggle to exclude it from the next message), 36 × 36 send button disabled when empty or busy, footer "Biatec AI can be wrong. Check before acting. Not financial advice."
- In: sign-in card restyled (secondary card, primary "Sign in with Biatec", explanation text) and the "sign-in unavailable here" variant with the tenant link.
- In: all new strings in the English catalog.
- Out: action preview, consent and receipt cards (T-042); data cards (T-067); header and shell (T-063).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-063: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Component tests: empty state shows four tier-appropriate prompts for Guest and for a signed-in fixture; clicking one sends it
- [ ] Streaming still renders progressively and the live region still announces completed messages (T-041 tests keep passing)
- [ ] Copy writes the plain text of the message to the clipboard and shows "Copied" for 1.5 s; Retry resends the last user message
- [ ] Context chip appears only when `pageContext` is set; when toggled off the next request carries no page context
- [ ] Enter / Shift+Enter / IME composition behave as specified (test with `isComposing`)
- [ ] No string literal in templates outside the i18n catalog (test)

## Implementation notes

- Do not render model output as HTML. Paragraph and bullet splitting is plain-text parsing; links only as text unless the backend marks them.
- Keep the busy/disabled logic from T-041; only presentation changes plus Copy, Retry and the context chip.

## Verification

pnpm --filter @biatec/chat-widget test; manual playground run against the local backend, screenshots of empty state and a streamed answer in dark and light

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-063: ask() goes through the chat composable in components/chat/**
- 2026-10-20 00:55 · kanban-board · blocked → backlog · blockers done: T-041, T-062
