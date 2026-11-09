---
id: T-236
title: "Widget: a user's Cancel is not a failure, and a sign-in or sign-out that finished before it is honoured (follow-up to T-235)"
status: done
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/src/**", "packages/chat-widget/test/**"]
parallel: yes
conflicts_with: []
issue: "#281"
branch: "task/T-236"
owner: "session-20261109-1130"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-11-09
updated: 2026-11-09
---

# T-236 - Widget: a user's Cancel is not a failure (follow-up to T-235)

## Context

The review of T-235 (PR #395) found that pressing Cancel just as a sign-in finishes refreshed the session but still showed "did not complete", a sign-out cancel showed "Sign-out did not complete" even when the logout had completed, and a Cancel during the opening step still sent the popup to the login page.

## Goal

Cancel returns the widget to idle with a neutral announcement; a flow that finished before the press is reported as it really ended.

## Scope

- In: `useChat` signIn and signOut cancel handling, strings (en, sk), tests.
- Out: the OIDC header (AlgorandGoogleDriveAccount issue 13).

## Acceptance criteria

- [ ] Cancel while waiting shows no failure text and announces "Sign-in cancelled" / "Sign-out cancelled".
- [ ] A sign-in that completed before the press is reported as signed in; a logout that completed before the press clears the conversation.
- [ ] A Cancel while opening never navigates the popup.
- [ ] A timeout is still reported as not completed.

## Verification

`cd packages/chat-widget && npx vitest run`

## Hand-off

Filled in when the task moves to review.

## Review

- 2026-11-09 18:16 · approve · PR #396 merged into main @ 508ac51 (recorded by kanban-sync)

## Activity log

- 2026-11-09 11:30 · scholtz · (new) → in-progress · created from the T-235 review and taken
- 2026-11-09 18:16 · user · in-progress → done · PR #396 merged into main @ 508ac51 (synced by kanban-sync)
