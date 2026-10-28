---
id: T-219
title: "Authenticator: settings tab, about and final accessibility polish"
status: blocked
priority: P1
epic: "Consent & authenticator"
blocked_by: [T-214, T-215, T-216, T-217, T-218, T-225]
area: ["apps/biatec_authenticator/lib/security/security_screen.dart", "apps/biatec_authenticator/lib/settings/**", "apps/biatec_authenticator/test/security/**", "apps/biatec_authenticator/test/a11y/**", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#355"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-24
---

# T-219 - Authenticator: settings tab, about and final accessibility polish

## Context

Spec: security settings (biometric requirement, threshold, blocked clients) and accessibility (large-type card, screen reader labels, no color-only warnings). The Settings tab is the fifth destination of the shell. It is last because it closes the redesign with an app-wide accessibility pass.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.


**Navigation change (T-225):** the bar is now Codes, Approvals, History, Devices, Settings. Trusted clients is no longer a tab: it is a screen opened from Settings, under Security. The Settings tab holds a row that opens Trusted clients, the lock timing (T-222) and the backup (T-226) entry; this task lays out the screen and leaves those rows to their tasks. Settings also shows the Biatec OIDC endpoint of every build and the device key level (the endpoint is not shown anywhere else for real builds).

## Goal

The Settings tab groups security settings, appearance and about information in a professional settings layout, and the whole app passes an accessibility and consistency review.

## Scope

- In: grouped settings sections (Security: biometric requirement, default threshold, blocked clients link; Appearance: system, light, dark theme choice kept in local storage; About: version, Biatec OIDC endpoint with mock or stage marking, device key level, open-source notices); an app-wide accessibility test suite (semantics, 48 dp targets, 3x text, dark mode) across every screen; README section on the navigation structure.
- Out: any change to the settings API.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Settings are grouped in labeled sections and every control has a description.
- [ ] The theme choice (system, light, dark) applies immediately and survives restart.
- [ ] About shows the app version, the Biatec OIDC base URL and whether it is mock, and the device key security level.
- [ ] `test/a11y/` checks, for every top-level screen, that tap targets are at least 48 dp and labels exist, and that nothing overflows at 3x text in light and dark.
- [ ] README documents the navigation structure.
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
- 2026-10-24 17:07 · kanban-board · backlog → blocked · waiting on: T-214, T-215, T-216, T-217, T-218
- 2026-10-24 12:05 · scholtz · taken · status stays blocked
