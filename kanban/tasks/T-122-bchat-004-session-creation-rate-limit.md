---
id: T-122
title: "Rate-limit unauthenticated session and conversation creation per address (BCHAT-004)"
status: in-progress
priority: P2
epic: "Security"
blocked_by: [T-121]
area: ["backend/Biatec.Chat.Api/Sessions/**", "backend/Biatec.Chat.Api/Auth/AuthEndpoints.cs", "backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs", "backend/Biatec.Chat.Api/Public/PublicChatEndpoints.cs", "backend/Biatec.Chat.Tests/Sessions/**"]
parallel: yes
conflicts_with: []
issue: "#257"
branch: "task/T-122"
owner: "session-20261022-2000"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-122 - Rate-limit unauthenticated session and conversation creation per address (BCHAT-004)

## Context

Security audit finding BCHAT-004 (Medium, issue #257). Each `GET /session`, `/session/probe`, `POST /auth/popup`,
`/session/adopt` and `/chat/messages` without a live cookie inserts a `Conversation`, a `ChatSession` and an evidence
entry; nothing throttles this per address. T-121 (BCHAT-005) makes the client address real behind ingress.

## Goal

The (N+1)th session-creating request from one address in a window gets 429.

## Scope

- In: a fixed-window per-address policy (`session-create`, configurable) applied to the five session-creating routes; tests.
- Out: materialising the session lazily; dropping evidence for unused anonymous sessions (alternative in the finding); BCHAT-035.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] The (N+1)th `GET /session/probe` from one address within the window returns 429; another address is unaffected.
- [ ] The other four routes carry the same policy.
- [ ] Existing session, auth, chat and public tests stay green.

## Verification

`dotnet test backend`.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-22 20:00 · claude · (new) → backlog · created from GitHub issue #257 (BCHAT-004); blocked_by T-121 satisfied (done)
- 2026-10-22 20:01 · scholtz · taken, and session-20261022-2000 · backlog → in-progress · claimed
