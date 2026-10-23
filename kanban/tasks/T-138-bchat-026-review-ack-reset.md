---
id: T-138
title: "Reset the review acknowledgement when what the user reviewed changes (BCHAT-026)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/src/components/cards/ActionPreviewCard.vue", "packages/chat-widget/test/cards.test.ts"]
parallel: yes
conflicts_with: []
issue: "#279"
branch: "task/T-138"
owner: "session-20261023-1212"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-138 - Reset the review acknowledgement when what the user reviewed changes (BCHAT-026)

## Context

Security audit finding BCHAT-026 (Low, issue #279). `ActionPreviewCard` resets its review state when the preview id or the awaiting step id
changes. A preview replaced in place under the same id and step id (new amounts, a new group hash, another receiver) keeps the earlier
"reviewed" and "acknowledged" flags, so Confirm stays enabled for content the user never reviewed. The real backend rebuilds with a new
preview id, so this needs a hostile or buggy backend, and the backend still checks the group hash on Confirm.

## Goal

Any change to what the review shows (intent, network, the decision step's group hash, accounts, assets, amounts, fee, slippage, simulation) clears the review and acknowledgement.

## Scope

- In: a fingerprint of the reviewed content in the reset watch of `ActionPreviewCard.vue`; tests.
- Out: changes that do not affect the review (status changes alone, countdown, the `limit` headroom).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] After review and acknowledgement, replacing the preview under the same id and step with a different amount, receiver, fee, group hash or intent disables Confirm again until it is reviewed.
- [ ] A status-only update (the same content) keeps the review.
- [ ] Existing card tests stay green.

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

- 2026-10-23 20:00 · claude · (new) → backlog · created from GitHub issue #279 (BCHAT-026); blockers scan: none
- 2026-10-23 12:12 · scholtz · taken, and session-20261023-1212 · backlog → in-progress · claimed
