---
id: T-211
title: "Authenticator: no binding code in the stored history, recovery code not selectable (BCHAT-031)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/lib/approvals/history_store.dart", "apps/biatec_authenticator/lib/enrollment/enrollment_screen.dart", "apps/biatec_authenticator/test/**", "apps/biatec_authenticator/native/README.md"]
parallel: yes
conflicts_with: []
issue: "#284"
branch: "task/T-211"
owner: "session-20261023-1404"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-211 - Authenticator: no binding code in the stored history, recovery code not selectable (BCHAT-031)

## Context

Security audit finding BCHAT-031 (Low, issue #284, MOB-7, RISK-003). The recovery code is shown once as `SelectableText`, which invites copy to the clipboard (readable by other apps, kept by keyboards and clipboard history). The approval history stores the whole signed receipt, and `signed.binding_code` is the code the user typed for that request; it stays in secure storage and is shown on the history screen. Screen-capture protection (Android `FLAG_SECURE`, iOS blur on resign-active) needs the platform projects, which are not in the repository yet (BCHAT-008).

## Goal

The binding code is never written to the history, and the recovery code cannot be selected or copied from the screen. The native screen-capture protection is written down for the platform work.

## Scope

- In: redacting `signed.binding_code` from a history entry when it is stored and when it is read (older entries lose it at the next write), a non-selectable recovery code, a note in native/README.md; tests.
- Out: the native FLAG_SECURE / blur code itself (no android/ios folders to put it in until BCHAT-008).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] A history entry stored or read back has no `binding_code`; the rest of the receipt, including its signature, is kept, and the entry says what was removed.
- [ ] The recovery code is shown as plain `Text` (no selection toolbar, no copy).
- [ ] `flutter analyze` and `flutter test` pass.

## Verification

`flutter analyze` and `flutter test` in apps/biatec_authenticator.

## Hand-off

## Review

## Activity log

- 2026-10-23 14:04 · kanban-split · (new) → backlog · created from audit issue #284
- 2026-10-23 14:04 · scholtz · taken · status stays backlog
- 2026-10-23 14:04 · session-20261023-1404 · backlog → in-progress · claimed, branch task/T-211
