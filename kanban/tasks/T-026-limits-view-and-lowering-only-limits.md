---
id: T-026
title: limits.view and lowering-only limits.update
status: blocked
priority: P2
epic: "Wallet tools"
blocked_by: [T-022]
area: ["backend/Biatec.Chat.Tools/Limits/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-19
updated: 2026-10-19
---

# T-026 · limits.view and lowering-only limits.update

## Context

limits.view via GET /wallet/limits; raising limits needs consent-gated OIDC changes that don't exist, so the fallback is lowering-only; hidden when tenant lacks manage-limits.

## Goal

Users can see and reduce their limits from chat.

## Scope

- In: Read executor, lowering-only update executor with preview (limits change hash), manage-limits elevation, tenant flag.
- Out: Raising limits.

## Acceptance criteria

- [ ] Limits shown with currency
- [ ] Lowering applies after confirmation
- [ ] Any raise is refused with explanation
- [ ] Hidden when tenant flag off

## Verification

dotnet test --filter Limits

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
