---
id: T-003
title: EF Core data model and migrations
status: blocked
priority: P0
epic: "Foundation"
blocked_by: [T-002]
area: ["backend/Biatec.Chat.Core/Data/**", "backend/Biatec.Chat.Api/Migrations/**"]
issue: "#4"
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#data-model-backend"
created: 2026-10-19
updated: 2026-10-19
---

# T-003 · EF Core data model and migrations

## Context

Data Model table lists Conversation, ChatSession, Message, ActionPreview, ActionStep, ConsentRequest, EvidenceEntry, EvidenceAnchor, EvidenceAnchorLeaf, PublicConversationState, PopupAuthRequest, PendingRevocation, Tenant, SafeApprovalCount, SoftCapSpend (TokenVaultEntry lives in Redis).

## Goal

PostgreSQL schema for all entities with migrations.

## Scope

- In: Entities and DbContext in Core with the listed fields, indexes for hot lookups (conversation+seq, session id, authReqId), initial migration, auto-migrate in Development.
- Out: Business logic using the entities.

## Acceptance criteria

- [ ] Migration applies cleanly to an empty postgres
- [ ] Every entity in the table exists with its key fields
- [ ] EvidenceEntry has a unique (conversationId, seq) index
- [ ] Integration test round-trips one row per entity

## Verification

dotnet ef database update; dotnet test --filter Data

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
