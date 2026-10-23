---
id: T-210
title: "Authenticator: refuse a non-HTTPS Biatec OIDC URL outside local mock use (BCHAT-029)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["apps/biatec_authenticator/lib/config.dart", "apps/biatec_authenticator/test/config_test.dart", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#282"
branch: "task/T-210"
owner: "session-20261023-1353"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-210 - Authenticator: refuse a non-HTTPS Biatec OIDC URL outside local mock use (BCHAT-029)

## Context

Security audit finding BCHAT-029 (Low, issue #282, MOB-4, RISK-003). `AppConfig.resolve` accepts any `OIDC_BASE_URL`, so a build can talk to Biatec OIDC over plain http (device token, binding data and receipts in the clear) or to a non-http scheme. The three HTTP clients use the default `http.Client()` with no further check.

## Goal

A release build only ever talks to an https Biatec OIDC URL; plain http is accepted for the built-in mock URL and, in non-release builds, for loopback and emulator hosts.

## Scope

- In: URL validation in `AppConfig.resolve` (fails at start with a clear message), tests, README note.
- Out: certificate pinning (SPKI) - a per-environment pin set and rotation plan is a separate decision.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `https://...` URLs resolve; `http://` is refused unless it is the built-in mock URL or (non-release only) a loopback, localhost or 10.0.2.2 host; any other scheme is refused.
- [ ] A URL with user info, a query or a fragment is refused.
- [ ] `flutter analyze` and `flutter test` pass.

## Verification

`flutter analyze` and `flutter test` in apps/biatec_authenticator.

## Hand-off

## Review

## Activity log

- 2026-10-23 13:53 · kanban-split · (new) → backlog · created from audit issue #282
- 2026-10-23 13:53 · scholtz · taken · status stays backlog
- 2026-10-23 13:53 · session-20261023-1353 · backlog → in-progress · claimed, branch task/T-210
