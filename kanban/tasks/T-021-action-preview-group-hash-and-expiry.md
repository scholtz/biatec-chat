---
id: T-021
title: Action preview, group hash and expiry
status: blocked
priority: P0
epic: "Wallet tools"
blocked_by: [T-003, T-004, T-005]
area: ["backend/Biatec.Chat.Core/Previews/**", "backend/Biatec.Chat.Tools/Algod/**"]
branch: ""
owner: ""
source: "PRODUCT-DESCRIPTION.md#action-preview"
created: 2026-10-19
updated: 2026-10-19
---

# T-021 · Action preview, group hash and expiry

## Context

Every fund-moving build yields a structured preview bound to the group hash (Algorand group id; assign grp to a lone txn via SDK); algod simulate; expiry from lastValid; Confirm disabled under 600 s; expired preview is rebuilt with a new hash; multi-step actions build later steps after earlier ones land.

## Goal

Shared preview machinery all capabilities use.

## Scope

- In: ActionPreview/ActionStep service, Algorand group id computation and grp assignment using the Algorand .NET SDK, simulate client per network, expiry and countdown fields, rebuild linkage (rebuiltAsPreviewId), multi-step state, preview DTO.
- Out: Bitcoin specifics (T-024), signing (T-022).

## Acceptance criteria

- [ ] Group hash equals SDK group id for test vectors
- [ ] Lone transaction gets grp without other field changes
- [ ] Preview carries expiry; confirm refused under 600 s
- [ ] Rebuild produces a new preview linked to the old

## Verification

dotnet test --filter Previews

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
