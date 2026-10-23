---
id: T-128
title: "Close the erasure bypass by a turn already in flight (BCHAT-013)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Core/Evidence/EvidenceLog.cs", "backend/Biatec.Chat.Core/Data/ConversationLock.cs", "backend/Biatec.Chat.Api/Retention/RetentionService.cs", "backend/Biatec.Chat.Api/Orchestration/ConversationOrchestrator.cs", "backend/Biatec.Chat.Tests/Retention/**", "backend/Biatec.Chat.Tests/Evidence/EvidenceTests.cs"]
parallel: yes
conflicts_with: []
issue: "#266"
branch: "task/T-128"
owner: "session-20261023-0953"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-128 - Close the erasure bypass by a turn already in flight (BCHAT-013)

## Context

Security audit finding BCHAT-013 (Low, issue #266). Erasure blanks a user's conversations in one transaction and moves their
sessions to fresh conversations. A turn already in flight keeps the old conversation id: its later evidence appends and
message stores write payloads and content into an erased conversation, with no `ErasedAt` guard.

## Goal

Nothing readable is written into a conversation after it was erased: appends and stores serialize with erasure and see `ErasedAt`.

## Scope

- In: a per-conversation advisory lock shared by erasure, evidence append and message store; an erased conversation gets evidence
  entries without payload and salt (the chain stays valid) and blank messages; tests.
- Out: ending the in-flight stream.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] Appending evidence to an erased conversation stores the entry without payload and salt, and the chain still verifies.
- [ ] Storing a message into an erased conversation stores no content, tool calls or tool results.
- [ ] Erasure takes the same per-conversation lock as the append (PostgreSQL).
- [ ] Existing retention, evidence and orchestration tests stay green.

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

- 2026-10-23 10:00 · claude · (new) → backlog · created from GitHub issue #266 (BCHAT-013); blockers scan: none
- 2026-10-23 09:53 · scholtz · taken, and session-20261023-0953 · backlog → in-progress · claimed
