---
id: T-015
title: Anonymous tier: requestSignIn and knowledge base search
status: blocked
priority: P0
epic: "Orchestrator"
blocked_by: [T-014]
area: ["backend/Biatec.Chat.Tools/Kb/**", "backend/Biatec.Chat.Api/Chat/SignIn/**", "docs/kb/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#session-tiers"
created: 2026-10-19
updated: 2026-10-19
---

# T-015 · Anonymous tier: requestSignIn and knowledge base search

## Context

Visitors get capability discovery; action requests get an explanation plus sign-in card, or a link to the tenant sign-in page where sign-in is unavailable; KB content arrives only as tool results.

## Goal

A useful anonymous assistant that cannot act.

## Scope

- In: kb.search over markdown files in docs/kb (simple ranked keyword search), seed KB pages about Biatec products, Algorand, DEX, Safe, fees, getting started; requestSignIn emitting sign-in card or sign-in-unavailable card with signInPageUrl.
- Out: Vector search.

## Acceptance criteria

- [ ] Asking what Biatec is returns an answer citing KB content
- [ ] Asking to move funds yields explanation + sign-in card and no wallet call
- [ ] Sign-in-unavailable variant works on public transport
- [ ] Tests cover both tools

## Verification

dotnet test --filter Kb; manual chat run

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
