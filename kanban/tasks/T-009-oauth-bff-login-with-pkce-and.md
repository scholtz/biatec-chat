---
id: T-009
title: OAuth BFF login with PKCE and popup
status: blocked
priority: P0
epic: "Sessions & auth"
blocked_by: [T-007, T-008]
area: ["backend/Biatec.Chat.Api/Auth/**"]
parallel: no
conflicts_with: [T-038]
issue: "#10"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#security-model-authorization-without-tokens-in-the-browser"
created: 2026-10-19
updated: 2026-10-19
---

# T-009 · OAuth BFF login with PKCE and popup

## Context

Backend is the confidential client; /auth/login and /auth/callback with state bound to session, nonce, PKCE, resource=https://mcp.biatec.io/tools; session id rotated after login; popup polled via authorization request id.

## Goal

Users sign in without any token reaching the browser.

## Scope

- In: PopupAuthRequest with one-time nonce; /auth/login (Sec-Fetch checks, nonce required), redirect to oidc.biatec.io/authorize scope openid profile email; /auth/callback verifies state and session, exchanges code with secret and verifier and resource, validates id_token, rotates session id, vaults tokens, marks popup complete; popup status endpoint. Mock OIDC in Tests.
- Out: Scope elevation (T-010), logout (T-011).

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-038: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Full flow passes against a test OIDC stub
- [ ] State from another session is rejected (login CSRF test)
- [ ] Browser responses never contain tokens or code exchange results
- [ ] Session id changes after login and old id is invalid

## Verification

dotnet test --filter Auth; manual run against stage OIDC once the client exists

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
