---
id: T-136
title: "Validate preview_card and consent_status frames and bound stream and reply size in the widget (BCHAT-025)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/src/transport/sse.ts", "packages/chat-widget/src/components/chat/useChat.ts", "packages/chat-widget/test/transport.test.ts", "packages/chat-widget/test/stream-limits.test.ts"]
parallel: yes
conflicts_with: []
issue: "#278"
branch: "task/T-136"
owner: "session-20261023-1147"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-136 - Validate preview_card and consent_status frames and bound stream and reply size in the widget (BCHAT-025)

## Context

Security audit finding BCHAT-025 (Low, issue #278). The widget's SSE reader checks `preview_card` only as "an object" and `consent_status`
for three fields; a malformed frame reaches `ActionPreviewCard.vue` and breaks the message list. The parser's buffer and the accumulated
reply text have no size limit. Only a hostile or buggy backend can send such frames.

## Goal

A `preview_card` or `consent_status` frame that does not match the contract is dropped; an oversized frame ends the stream with an error
event; a reply cannot grow without bound.

## Scope

- In: `isActionPreview` / `isActionStep` / `isConsentStatus` guards matching the generated protocol types; a frame size limit in the SSE parser; a reply text cap in `useChat`; tests.
- Out: BCHAT-026 (review acknowledgement), the other cards' guards (already present).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] Valid preview and consent frames from the existing tests still pass through.
- [ ] A preview with a missing or mistyped field (steps not an array, unknown status, non-string groupHash, malformed asset) is dropped.
- [ ] A consent frame with an unknown state or non-string optional field is dropped.
- [ ] A frame above the size limit ends the stream with an `error` event and does not buffer further.
- [ ] Reply text stops growing at the cap.
- [ ] The widget test suite shows no new failures (five pre-existing DEX playground failures are BCHAT-045).

## Verification

`pnpm --filter @biatec/chat-widget test`.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 18:00 · claude · (new) → backlog · created from GitHub issue #278 (BCHAT-025); blockers scan: none
- 2026-10-23 11:47 · scholtz · taken, and session-20261023-1147 · backlog → in-progress · claimed
