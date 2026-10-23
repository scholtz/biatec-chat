---
id: T-208
title: "Sign-in popups: COOP on our page, a widget that survives a severed opener, and the need recorded for Biatec OIDC (BCHAT-028)"
status: in-progress
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

# T-208 - Sign-in popups: COOP on our page, a widget that survives a severed opener, and the need recorded for Biatec OIDC (BCHAT-028)

## Context

Security audit finding BCHAT-028 (Low, issue #281, WDG-5, RISK-007). The widget opens the sign-in popup with `window.open`, so every page in the popup (our `/auth/callback` close page, and Biatec OIDC's login pages) can reach the host page through `window.opener` (reverse tabnabbing). The audit asks for `Cross-Origin-Opener-Policy: same-origin` on the popup pages. That header severs the opener relation, after which the opener sees `popup.closed === true` at once, and the widget currently treats `closed` as the user cancelling: it would fail every sign-in as soon as any popup page sets the header. Completion itself is learned by polling, not through the popup.

## Goal

Our popup page sends COOP, the widget no longer misreads a severed popup as a cancelled sign-in, and the request for COOP on Biatec OIDC's pages is recorded where the OIDC owners read it.

## Scope

- In: COOP on the backend's close page; the widget's popup-closed detection (a popup that was never seen open is not a cancel); the required-changes entry; tests.
- Out: opening the popup with `noopener` (the blank popup must be navigated after the backend answers); Biatec OIDC's own headers (another repository).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] The backend's `/auth/callback` close page sends `Cross-Origin-Opener-Policy: same-origin`.
- [ ] A popup the widget never saw open (severed by COOP) does not end the sign-in as cancelled; completion by polling still works, and a popup the user closes after it was seen open still ends it.
- [ ] docs/required-changes-biatec-oidc.md asks Biatec OIDC to send COOP on its authorization pages, with the consequence for the widget.

## Verification

`pnpm --filter @biatec/chat-widget test`, `dotnet test backend --filter Auth`, full suite in CI.

## Hand-off

## Review

## Activity log

- 2026-10-23 13:33 · kanban-split · (new) → backlog · created from audit issue #281
- 2026-10-23 13:33 · scholtz · taken · status stays backlog
- 2026-10-23 13:33 · session-20261023-1333 · backlog → in-progress · claimed, branch task/T-208
