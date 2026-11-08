---
id: T-235
title: "Widget: Cancel button while waiting for sign-in, no longer read popup.closed (BCHAT-028 prerequisite)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/src/**", "packages/chat-widget/test/**", "packages/chat-widget/CHANGELOG.md", "docs/required-changes-biatec-oidc.md"]
parallel: yes
conflicts_with: []
issue: "#281"
branch: "task/T-235"
owner: "session-20261108-1830"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-11-08
updated: 2026-11-08
---

# T-235 - Widget: Cancel button while waiting for sign-in, no longer read popup.closed (BCHAT-028 prerequisite)

## Context

Issue #281 (BCHAT-028). T-208 recorded that Biatec OIDC should send `Cross-Origin-Opener-Policy: same-origin` on its popup pages, and that the widget must first stop treating `popup.closed` as the cancel signal (with the header the opener reads it as true at once). See docs/required-changes-biatec-oidc.md, "Popup pages and window.opener".

## Goal

The widget ends a waiting sign-in or sign-out by status polling, the timeout, or the user's own Cancel button, never by `popup.closed`.

## Scope

- In: a Cancel button in the shell header while the widget waits (sign-in, elevation, sign-out); `useChat.cancelSignIn`; removal of the `popup.closed` checks; strings (en, sk); tests; changelog; the spec doc.
- Out: the header itself (Biatec OIDC, AlgorandGoogleDriveAccount issue 13).

## Parallel work

Parallel work possible: **yes**.

## Acceptance criteria

- [ ] While waiting, the header shows Cancel; pressing it ends the wait, closes the popup and shows the failed state.
- [ ] A popup whose `closed` reads true does not end the wait.
- [ ] The widget suite passes.

## Verification

`cd packages/chat-widget && npx vitest run`

## Hand-off

Filled in when the task moves to review.

## Review

## Activity log

- 2026-11-08 18:30 · scholtz · (new) → in-progress · created for issue #281 and taken
