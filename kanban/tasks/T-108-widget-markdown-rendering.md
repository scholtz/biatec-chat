---
id: T-108
title: "Widget: render assistant replies as safe Markdown"
status: in-progress
priority: P1
epic: "Widget"
blocked_by: []
area: ["packages/chat-widget/src/components/chat/format.ts", "packages/chat-widget/src/components/chat/ChatView.vue", "packages/chat-widget/src/components/chat/chat.css", "packages/chat-widget/test/chat-markdown.test.ts"]
parallel: yes
conflicts_with: []
issue: "#229"
branch: "task/T-108"
owner: "session-20261020-1007"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#chat-widget-biatecchat-widget"
created: 2026-10-20
updated: 2026-10-20
---

# T-108 · Widget: render assistant replies as safe Markdown

## Context

Found in demo testing (2026-10-20): the model answers in Markdown (`**bold**`, numbered lists, `inline code`,
headings), but the widget shows the raw syntax. `packages/chat-widget/src/components/chat/format.ts` `parseBlocks`
only splits paragraphs and `-`/`*`/`•` bullet lists; everything else is rendered as literal text in
`ChatView.vue` (around line 195). Example from the demo reply: `1. **How much EURS** do you want to deposit?`
shows the asterisks.

Security invariant (AGENTS.md, spec): model output is untrusted and is **never interpreted as HTML**. The comment
in `ChatView.vue` ("Text only: model output is never interpreted as HTML") must stay true.

## Goal

Assistant replies show common Markdown formatting, with no path from model text to raw HTML or script.

## Scope

- In: extend the parser to a small, safe Markdown subset rendered as Vue nodes (no `v-html`): bold, italic,
  inline code, fenced code blocks, ordered and unordered lists (incl. one nesting level), headings (rendered as
  bold paragraphs or small headings), links only through `safeHttpUrl` (`rel="noopener noreferrer"`,
  `target="_blank"`), line breaks. Styles in `chat.css` using the existing theme tokens, light and dark.
  Streaming: partial Markdown mid-stream must not throw or flicker badly (an unclosed `**` renders as text).
- Out: tables, images, raw HTML (always shown as text), user-message formatting (user text stays plain), cards.
  A library is fine (for example `markdown-it` with `html: false` producing tokens you render yourself) as long as
  the output is never set via `innerHTML`/`v-html`; prefer a tiny hand-written parser if it stays readable.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes the widget's chat view or its formatter.

## Acceptance criteria

- [ ] `**bold**`, `*italic*`, `` `code` ``, ```` ``` ```` blocks, `1.` lists, `-` lists and `#` headings render as
      formatted text in an assistant message.
- [ ] `<script>`, `<img onerror=…>` and other HTML in model text render as visible literal text; no `v-html` or
      `innerHTML` is used for model text (grep shows none).
- [ ] `[x](javascript:alert(1))` is not a link; `[x](https://biatec.io)` is a link opening in a new tab.
- [ ] Unit tests in `packages/chat-widget/test/chat-markdown.test.ts` cover the above, plus an unclosed marker
      during streaming; existing `chat-conversation.test.ts` still passes.
- [ ] Copy-to-clipboard still copies the original Markdown text.

## Verification

`pnpm --filter @biatec/chat-widget test` and `pnpm --filter @biatec/chat-widget build`; open the DEX playground
(or the mock backend) and ask a question that produces a numbered list with bold text; screenshot light and dark.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-20 12:00 · coordinator · (new) → backlog · created from demo testing (raw Markdown in replies)
- 2026-10-20 10:07 · LukassSoukup · taken · status stays backlog
- 2026-10-20 10:07 · session-20261020-1007 · backlog → in-progress · claimed in session
