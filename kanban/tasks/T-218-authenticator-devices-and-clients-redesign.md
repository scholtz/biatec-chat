---
id: T-218
title: "Authenticator: redesign devices and trusted clients tabs"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214]
area: ["apps/biatec_authenticator/lib/devices/**", "apps/biatec_authenticator/lib/clients/**", "apps/biatec_authenticator/lib/security/settings_frame.dart", "apps/biatec_authenticator/test/settings/**"]
parallel: yes
conflicts_with: []
issue: "#354"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-24
---

# T-218 - Authenticator: redesign devices and trusted clients tabs

## Context

Spec: device management and trusted clients with per-client thresholds. T-084 made these screens work against the mock; the settings API exists in the mock only (see docs/required-changes-biatec-oidc.md rows 21 and 22), so the screens must keep their 'not available on this server' state.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.

## Goal

Devices and Clients are polished list-detail screens with clear current-device marking, revoke and threshold editing flows, and honest unavailable states.

## Scope

- In: devices list with this-device badge (icon plus text), key security level, last seen, revoke with a confirmation sheet; clients list with trust state, per-client threshold editing in a bottom sheet with validation and the currency or unit shown, block and unblock with confirmation; settings frame restyle (loading, error with retry, unavailable-on-this-server); widget tests.
- Out: the settings API and controller (`settings_controller.dart`, `settings_client.dart`).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] The current device is marked and cannot be revoked from itself without an explicit warning.
- [ ] Revoke, block and unblock need a confirmation and show the result.
- [ ] Threshold editing validates input and shows inline errors; the saved value is announced.
- [ ] Where the server has no settings API, the screens say so and offer no dead controls.
- [ ] Existing settings controller and screen tests pass (updated only for changed widgets).
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
