---
id: T-113
title: "Bug: an expired session silently turns a signed-in chat anonymous"
status: review
priority: P1
epic: "Auth"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs", "backend/Biatec.Chat.Tests/Chat/**", "packages/chat-widget/src/transport/cookie.ts", "packages/chat-widget/src/components/chat/useChat.ts", "packages/chat-widget/src/i18n/*/chat.json", "packages/chat-widget/test/**"]
parallel: yes
conflicts_with: []
issue: "#238"
branch: "task/T-113"
owner: "session-20261020-1150"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-113 · Bug: an expired session silently turns a signed-in chat anonymous

## Context

Found in demo testing (2026-10-20). The widget header showed the user as signed in, yet:

> **User:** Add liquidity to the Biatec DEX EURS/USDC pool.
> **AI:** tool status "Preparing sign-in: Done", then "To add liquidity to the EURS/USDC pool, you'll need to sign
> in first. Please use the sign-in button above." and the sign-in card.

`requestSignIn` is only reached when the session tier is `Anonymous` (`ToolGate` `tier_too_low`, or the executors'
own tier check). The access token isn't the problem: `TokenVault.GetAccessTokenAsync` refreshes it on demand.
The **cookie session** is: the spec ("No tokens in the browser") gives it a 15-minute idle timeout. Once that
lapses, `POST /chat/messages` → `SessionService.ResolveOrCreateAsync` silently creates a new anonymous session (and
conversation), sets its cookie and runs the turn on it. The widget only reads `GET /session` on panel open and
after sign-in/out, so the header keeps saying "signed in" while the backend treats the user as a guest.

No cluster context on this machine, so the pod logs weren't read; the cause follows from the code path above.

## Goal

A signed-in user whose session expired is told so, sees the header drop to the anonymous tier and gets the sign-in
card, instead of an action request being answered as if they had never signed in.

## Scope

- In: `/chat/messages` refuses (401, problem `code: session_expired`, new anonymous cookie set) when the request
  carried a well-formed session cookie that no longer resolves to a live session, instead of running the turn. The
  cookie transport surfaces that code; `useChat.send` then refreshes the session state, and: if the widget thought it
  was signed in, shows a one-line "session ended, sign in again" note with the sign-in card; if it was anonymous,
  retries the message once on the new session without a note.
- Out: changing the idle timeout or absolute lifetime (spec values); keepalive while the panel is merely open;
  the public transport.

## Parallel work

Parallel work possible: **yes**. The only other in-progress task (T-112) touches the KB, not the chat endpoint or the
widget transport.

## Acceptance criteria

- [x] `/chat/messages` with an expired/unknown well-formed cookie returns 401 `session_expired`, sets a new cookie and
      runs no model turn; a request without a cookie still starts a session and answers (tests).
- [x] The widget, on `session_expired` while signed in, refreshes the session (header shows anonymous) and shows the
      expiry note with the sign-in card; while anonymous it retries once transparently (tests).
- [x] en and sk strings for the note.
- [x] `dotnet test` (chat tests) and the widget tests pass.

## Verification

`dotnet test backend/Biatec.Chat.slnx --filter Chat`, `pnpm --filter @biatec/chat-widget test`. Manual: sign in on
stage, wait 15 minutes idle (or delete the session row), send "Add liquidity…" → expiry note and sign-in card,
header anonymous.

## Hand-off

- **Summary:** cause is the 15-minute idle timeout of the cookie session (spec), not the access token (the vault
  refreshes that on demand). An ended session made `/chat/messages` start a new anonymous session silently and the
  widget never re-read `/session`. Now `/chat/messages` answers 401 problem `code: session_expired` (new anonymous
  cookie set, no model turn) when the request carried a well-formed cookie that doesn't resolve to a live session.
  The cookie transport puts the problem code on `TransportError`; `useChat.send` refreshes the session and, if the
  user was signed in, shows "Your session ended … sign in again; your message was not sent" with the sign-in card
  (header drops to Guest); a guest's message is retried once on the new session without a note.
- **Files changed:** `backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs`; `packages/chat-widget/src/transport/{cookie,types}.ts`,
  `src/components/chat/useChat.ts`, `src/i18n/{en,sk}/chat.json`. Tests: `backend/Biatec.Chat.Tests/Orchestration/OrchestrationTests.cs`
  (outside the listed `Tests/Chat/**` area, which doesn't exist; the chat endpoint tests live there),
  `packages/chat-widget/test/{chat-conversation,transport}.test.ts`.
- **How it was verified:** `dotnet test backend/Biatec.Chat.slnx` 1326 passed, 1 skipped; widget `vitest run` 380
  passed, 5 skipped; `vue-tsc` shows only the 3 playground errors that `main` already has. Pod logs weren't read (no
  cluster context here). Manual stage check pending the deploy.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** none. Note: the idle timeout itself still applies while the panel is open and the user is
  only reading; that's the spec's 15 minutes, now reported instead of hidden.

## Review

## Activity log

- 2026-10-20 11:50 · session-20261020-1150 · (new) → backlog · created from demo testing (signed-in chat asked to sign in)
- 2026-10-20 11:50 · LukassSoukup · taken · status stays backlog
- 2026-10-20 11:50 · session-20261020-1150 · backlog → in-progress · claimed in session
- 2026-10-20 12:05 · session-20261020-1150 · in-progress → review · tests green, PR opened
