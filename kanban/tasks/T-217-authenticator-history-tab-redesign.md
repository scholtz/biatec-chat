---
id: T-217
title: "Authenticator: redesign approval history and receipt detail"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214]
area: ["apps/biatec_authenticator/lib/approvals/history_screen.dart", "apps/biatec_authenticator/lib/approvals/history_widgets/**", "apps/biatec_authenticator/test/approvals/history_*"]
parallel: yes
conflicts_with: []
issue: "#353"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-24
---

# T-217 - Authenticator: redesign approval history and receipt detail

## Context

Spec: approval history. History is stored locally (redacted, see `history_redaction_test.dart`) and must stay redacted. The tab needs a modern list with grouping, filters and a detail view of the receipt.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.

## Goal

History is a scannable, grouped list with status filters and a receipt detail screen, with clear empty and error states.

## Scope

- In: list grouped by day with status chips (approved, denied, expired) that have icon and text, filter chips, a detail screen (what was approved, when, client, receipt id and device key id with copy), empty state, and a clear-history action with confirmation if the store already supports it.
- Out: the store format and redaction rules (`history_store.dart`); syncing history from the server.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] History groups entries by day and filters by status.
- [ ] The detail screen shows the redacted receipt fields only (the redaction test still passes) and offers copy of the receipt id.
- [ ] Empty and error states use the shared widgets.
- [ ] Existing history tests pass unchanged.
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
