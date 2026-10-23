---
id: T-208
title: "Record the popup window.opener exposure and the COOP prerequisite for Biatec OIDC (BCHAT-028)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/src/components/chat/useChat.ts", "packages/chat-widget/test/**", "backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Tests/Auth/**", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#281"
branch: "task/T-208"
owner: "session-20261023-1333"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-208 - Record the popup window.opener exposure and the COOP prerequisite for Biatec OIDC (BCHAT-028)

## Context

Security audit finding BCHAT-028 (Low, issue #281, WDG-5, RISK-007). The widget opens the sign-in popup with `window.open`, so every page in the popup (our `/auth/callback` close page, and Biatec OIDC's login pages) can reach the host page through `window.opener` (reverse tabnabbing). The audit asks for `Cross-Origin-Opener-Policy: same-origin` on the popup pages. That header severs the opener relation, after which the opener sees `popup.closed === true` at once, and the widget currently treats `closed` as the user cancelling: it would fail every sign-in as soon as any popup page sets the header. Completion itself is learned by polling, not through the popup.

## Goal

The exposure and the exact change needed at Biatec OIDC are written down where the OIDC owners read them, together with what the widget must do before the header is switched on. Nothing in the sign-in flow changes in this task.

## Scope

- In: a section in docs/required-changes-biatec-oidc.md.
- Out: any code change. Analysis (below) found none that is safe without a real-browser check this session cannot do:
  - COOP on our own close page protects nothing (it is our page, the last one shown) and would stop the popup from closing itself in some browsers.
  - COOP only helps on the third-party pages (Biatec OIDC's authorization pages). Once they send it, the opener sees the popup as `closed` immediately, which the widget reads as "the user cancelled", so sign-in would fail at once. The widget needs a cancel signal that does not depend on `popup.closed` (for example a Cancel button while waiting, relying on the status polling and the 5 minute timeout) in the same release.
  - `noopener` is not possible: the blank popup is opened synchronously from the click and navigated after the backend answers.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] docs/required-changes-biatec-oidc.md has a section naming the exposure, the header Biatec OIDC should send on its authorization pages, and the widget change that must ship with it.

## Verification

`pnpm --filter @biatec/chat-widget test`, `dotnet test backend --filter Auth`, full suite in CI.

## Hand-off

## Review

- 2026-10-23 13:39 · approve · PR #335 merged into main @ d6f9935 (recorded by kanban-sync)

## Activity log

- 2026-10-23 13:33 · kanban-split · (new) → backlog · created from audit issue #281
- 2026-10-23 13:33 · scholtz · taken · status stays backlog
- 2026-10-23 13:33 · session-20261023-1333 · backlog → in-progress · claimed, branch task/T-208
- 2026-10-23 13:39 · user · in-progress → done · PR #335 merged into main @ d6f9935 (synced by kanban-sync)
