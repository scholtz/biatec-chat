---
id: T-126
title: "Administrator dashboard for ADMINS: OIDC sign-in, user list, impersonation, statistics"
status: in-progress
priority: P1
epic: "Deploy & docs"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Tests/Admin/**", "apps/admin/**", "backend/Dockerfile", "docs/admin-dashboard.md"]
parallel: yes
conflicts_with: []
issue: "#308"
branch: "task/T-126"
owner: "session-20261023-0900"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#security-invariants"
created: 2026-10-23
updated: 2026-10-23
---

# T-126 - Administrator dashboard for ADMINS: OIDC sign-in, user list, impersonation, statistics

## Context

Operators today use bearer keys (`Admin:Operators`, docs/admin-api.md) for tenant policy and knowledge-base pages. There
is no human-facing admin surface. The repository owner created the GitHub secret `ADMINS` (emails separated by `;`):
those people must reach an administrator dashboard after signing in through Biatec OIDC, list users, impersonate any
user and see statistics.

Security invariants that shape the design (AGENTS.md): the browser never receives an access, refresh or ID token;
signing goes only from the backend to Biatec OIDC; tier and scope gates are enforced server side. So the dashboard
signs in with the tenant's own OIDC client (the same registered redirect URI `/auth/callback`, a new `admin` purpose),
validates the ID token, keeps **no** token, and issues an opaque, server-side admin session cookie. "Impersonation" is a
read-only **view as user**: it shows what the user sees (conversations, transcripts, action previews), never acts as them.

## Goal

Admins in `ADMINS` sign in with Biatec OIDC to a dashboard served by the backend at `/admin/` with overview statistics,
a user list, user detail and audited read-only impersonation.

## Scope

- In: `Admin:Admins` option (fed by `ADMINS`), admin OIDC login/callback/logout, Redis admin sessions, admin JSON API
  (`/admin/api/*`), `KnownUser` table (subject, first/last seen; no email, the product stores none) filled at sign-in and removed on erasure, statistics,
  impersonation with reason, expiry and evidence records, Vue 3 SPA `apps/admin` served from the backend image, deploy
  wiring for the `ADMINS` secret, documentation, tests.
- Out: acting as a user (sending, confirming, signing), editing users, admin roles beyond "admin", changes to Biatec OIDC.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Only emails in `ADMINS` (case-insensitive, `;` separated) get an admin session; a verified-email check applies; any other user, an unverified email or a failed validation gets no session.
- [x] No OIDC token reaches the browser or is stored; the admin cookie is opaque, HttpOnly, Secure, SameSite=Strict; state-changing calls need the custom header and a same-origin Origin.
- [x] Statistics endpoint and dashboard show users, conversations, messages, sessions by tier, previews by status, token usage and a daily series.
- [x] User list is searchable, sortable and paged; user detail shows conversations and transcripts.
- [x] Impersonation needs a reason, expires, is read-only, shows a banner, and start/stop are recorded in the admin evidence chain.
- [x] Erasing a user removes the `KnownUser` row.
- [x] The `ADMINS` secret reaches the backend in the deploy workflow and k8s manifest; docs/admin-dashboard.md documents setup, security model and API.
- [x] Backend and admin app tests pass; Docker image builds the SPA into `/admin`.

## Verification

`dotnet test backend --filter Admin`, the full backend suite, `pnpm --filter @biatec/admin test build`.

## Hand-off

## Review

## Activity log

- 2026-10-23 09:00 · kanban-split · (new) → backlog · created from the repository owner's request (issue #308)
- 2026-10-23 09:01 · scholtz · taken · status stays backlog
- 2026-10-23 09:02 · session-20261023-0900 · backlog → in-progress · claimed, branch task/T-126
