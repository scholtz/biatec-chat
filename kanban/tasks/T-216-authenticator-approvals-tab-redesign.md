---
id: T-216
title: "Authenticator: redesign pending approvals and the approval card"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214]
area: ["apps/biatec_authenticator/lib/approvals/pending_screen.dart", "apps/biatec_authenticator/lib/approvals/approval_screen.dart", "apps/biatec_authenticator/lib/approvals/approval_card.dart", "apps/biatec_authenticator/lib/approvals/widgets/**", "apps/biatec_authenticator/test/approvals/approval_*", "apps/biatec_authenticator/test/approvals/pending_*", "apps/biatec_authenticator/test/approvals/support.dart"]
parallel: yes
conflicts_with: []
issue: "#352"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-29
---

# T-216 - Authenticator: redesign pending approvals and the approval card

## Context

Spec: pending approvals list, the approval card ('what you see is what you sign'), large-type card, screen-reader labels for amounts and names, no color-only warnings, offline mode that refuses to approve without a live fetch. The controllers, client, receipts and canonicalisation stay untouched; only presentation changes.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.

## Goal

The Approvals tab and the approval card are clear, trustworthy and fast to act on: the amount, recipient and requesting client are the hero, the binding code is prominent, and Approve and Deny are unmistakable.

## Scope

- In: pending list with request cards (client, action summary, amount, time left as text and a progress ring), pull to refresh, empty, error and offline states; approval card with a hero amount (`AmountText`), recipient and network, requesting client and trust badge, the binding code in a large monospace group with 'must match your screen' text, risk and first-use warnings as `StatusBanner` (icon plus text), a bottom action bar with Approve (biometric prompt) and Deny, a confirmation after the receipt is posted, and an expired or already-decided state; widget tests for each state.
- Out: protocol, receipts, controller logic and the biometric gate implementation.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] The card renders only the fetched authorization details, shows the binding code, amount, recipient, client and expiry, and announces them in full to a screen reader (semantics test).
- [ ] Offline shows a banner and disables Approve with the reason; Deny stays disabled too until a live fetch succeeds.
- [ ] Warnings use an icon and text, never color alone.
- [ ] Approve and Deny are in a sticky bottom bar reachable with one hand and remain visible at 3x text scale.
- [ ] Expired, already-decided and failed-post states are specific screens or banners with a next action.
- [ ] Existing approval controller and contract tests pass unchanged.
- [ ] Every interactive element is at least 48x48 dp, has a screen-reader label (amounts and names are announced in full), and no state is shown by color alone.
- [ ] Layouts hold at 3x system text scale and in light and dark mode without overflow (widget tests at textScaler 1.0 and 3.0).
- [ ] `flutter analyze` is clean and `flutter test` passes, including new widget tests for every new or redesigned screen state (loading, empty, error, offline, data).

## Implementation notes

Presentation only: services, controllers, clients and the consent protocol stay as they are. Keep widget keys that existing tests use, or update those tests in the same change. Use only packages already in `pubspec.yaml` unless the task says otherwise (Material 3 provides everything needed). Run `flutter analyze` and `flutter test` in `apps/biatec_authenticator`; build with `./build-windows.ps1` to see it.

## Verification

`cd apps/biatec_authenticator && flutter analyze && flutter test`, then `./build-windows.ps1` and a manual look at the changed screens in light and dark mode.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

Written only by the user (or by `/kanban-review` on the user's behalf).

## Activity log

- 2026-10-24 12:00 · claude · (new) → blocked · created from the authenticator redesign request
- 2026-10-24 17:07 · kanban-board · backlog → blocked · waiting on: T-214
- 2026-10-24 12:05 · scholtz · taken · status stays blocked
- 2026-10-29 16:26 · kanban-board · blocked → backlog · blockers done: T-214
