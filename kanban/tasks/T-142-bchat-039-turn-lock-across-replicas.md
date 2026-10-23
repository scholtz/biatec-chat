---
id: T-142
title: "Serialise a conversation's turns across replicas, not only in one process (BCHAT-039)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Api/Chat/ChatEndpoints.cs", "backend/Biatec.Chat.Api/Chat/ConversationTurnLock.cs", "backend/Biatec.Chat.Tests/Orchestration/ConversationTurnLockTests.cs"]
parallel: yes
conflicts_with: []
issue: "#292"
branch: "task/T-142"
owner: "session-20261023-1414"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-142 - Serialise a conversation's turns across replicas, not only in one process (BCHAT-039)

## Context

Security audit finding BCHAT-039 (Low, issue #292). `POST /chat/messages` allows one turn at a time per conversation with an in-process dictionary, and
the orchestrator allocates message `Seq` numbers as `max(Seq) + 1`. With two backend replicas, two requests for one conversation can run at
once and collide on the (conversation, seq) primary key mid-turn or interleave history.

## Goal

At most one turn per conversation runs at a time across all replicas; the second request gets the same 409 as today.

## Scope

- In: a PostgreSQL session advisory lock (`pg_try_advisory_lock`) held on a dedicated, non-pooled connection for the duration of the turn, taken after the in-process check; released on completion, failure or client abort; tests with real PostgreSQL.
- Out: changing the Seq allocation; the public chat transport.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] A second acquire for the same conversation fails while the first is held, from a separate database connection (as another replica would be); a different conversation can be acquired; after release it can be acquired again.
- [x] Disposing releases the lock even if the unlock statement is not reached (the dedicated connection is closed, not pooled).
- [x] The chat endpoint answers 409 for a turn held by another replica, and releases the lock when the turn ends.
- [x] Existing chat and orchestration tests stay green.

## Verification

`dotnet test backend`.

## Hand-off

- **Summary:** A turn now also takes a PostgreSQL session advisory lock (pg_try_advisory_lock on 'turn:<conversation id>') on a dedicated, non-pooled connection for its whole duration, after the existing in-process check; another replica's turn on the same conversation gets the same 409. Closing the connection always releases the lock, so a crash or abort cannot leave a conversation locked. Other providers get a no-op handle.
- **Files changed:** Api/Chat/ConversationTurnLock.cs (new), Api/Chat/ChatEndpoints.cs, Tests/Orchestration/ConversationTurnLockTests.cs, Tests/Orchestration/OrchestrationTests.cs.
- **How it was verified:** Real-PostgreSQL tests with separate connections as separate replicas: second acquire fails while held and works after release, another conversation is unaffected, the turn lock does not collide with the short per-write ConversationLock (T-128), release is idempotent; an endpoint test where an external holder makes the chat endpoint answer 409 without calling the model and the next turn runs after release. Full backend suite run before the PR.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The Seq allocation itself (max+1) is unchanged: with the turn lock it is serialised per conversation, but a writer that bypasses the endpoint (the public chat transport has its own in-process guard) is not covered by this lock. Want the public transport to take it too?

## Review

## Activity log

- 2026-10-23 23:59 · claude · (new) → backlog · created from GitHub issue #292 (BCHAT-039); blockers scan: none
- 2026-10-23 14:14 · scholtz · taken, and session-20261023-1414 · backlog → in-progress · claimed
