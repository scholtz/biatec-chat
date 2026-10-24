---
id: T-214
title: "Authenticator: design system and bottom navigation shell"
status: backlog
priority: P1
epic: "Consent & authenticator"
blocked_by: []
area: ["apps/biatec_authenticator/lib/main.dart", "apps/biatec_authenticator/lib/routes.dart", "apps/biatec_authenticator/lib/theme.dart", "apps/biatec_authenticator/lib/screens/**", "apps/biatec_authenticator/lib/ui/**", "apps/biatec_authenticator/test/app_test.dart", "apps/biatec_authenticator/test/ui/**"]
parallel: yes
conflicts_with: []
issue: "#350"
branch: ""
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#authenticator-app-flutter"
created: 2026-10-24
updated: 2026-10-24
---

# T-214 - Authenticator: design system and bottom navigation shell

## Context

The authenticator today opens on a plain list of links (`HomeScreen`) and pushes every screen on a single stack. A professional mobile app needs persistent, icon-based bottom navigation, a coherent design system and consistent loading, empty, error and offline states. This task builds that foundation; the screen redesigns (T-215 to T-219) plug into it.

Read first: `PRODUCT-DESCRIPTION.md` (Authenticator App (Flutter)), `AGENTS.md`, `apps/biatec_authenticator/README.md`. The redesign follows current Material 3 and mobile UX practice: bottom navigation with icons and labels for top-level destinations, one primary action per screen, clear states (loading, empty, error, offline), accessible by default.

## Goal

The app opens in a Material 3 shell with a bottom navigation bar (icons and labels) for the five main areas, a shared design system, and shared state widgets, and the existing screens run inside it unchanged.

## Scope

- In: tokens in `theme.dart` (brand color scheme, typography scale, spacing, shape, elevation, light and dark); `lib/ui/` shared widgets: `SectionHeader`, `EmptyState`, `ErrorState` (with retry), `LoadingSkeleton`, `StatusBanner` (info, warning, danger; always icon plus text), `OfflineBanner`, `AmountText`; an `AppShell` with a `NavigationBar` of Approvals, History, Devices, Clients and Settings (pending-count badge on Approvals), one `Navigator` per tab so each tab keeps its state, a `NavigationRail` from 840 dp width (desktop and tablets); routes become tab destinations; an enrollment gate (not enrolled shows the existing enrollment screen full screen, without the bar); the home list and `routes.dart` list are removed; the push 'Open' action and tapped notifications select the Approvals tab.
- Out: restyling the content of the individual screens (T-215 to T-219).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] The app starts in the shell with a `NavigationBar` of five destinations, each with an icon, a label and a tooltip; the selected destination is clear without color alone.
- [ ] Switching tabs keeps each tab's scroll position and pushed screens (per-tab navigators); Android back pops inside the tab first.
- [ ] Width of 840 dp or more shows a `NavigationRail` instead of the bar.
- [ ] A device that is not enrolled sees the enrollment screen without navigation; after enrollment it sees the shell.
- [ ] A push notification action and a foreground 'New approval request' snackbar select the Approvals tab (no duplicate stacked list).
- [ ] `lib/ui/` provides `SectionHeader`, `EmptyState`, `ErrorState`, `LoadingSkeleton`, `StatusBanner`, `OfflineBanner`, `AmountText` with widget tests, and the theme exposes the tokens in light and dark.
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

- 2026-10-24 12:00 · claude · (new) → backlog · created from the authenticator redesign request
- 2026-10-24 12:05 · scholtz · taken · status stays backlog
