---
id: T-004
title: RFC 8785 canonical JSON and hashing library
status: done
priority: P0
epic: "Foundation"
blocked_by: [T-001]
area: ["backend/Biatec.Chat.Core/Canonical/**", "backend/Biatec.Chat.Tests/Canonical/**"]
parallel: yes
conflicts_with: []
issue: "#5"
branch: "task/T-004"
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#evidence-record"
created: 2026-10-19
updated: 2026-10-19
---

# T-004 · RFC 8785 canonical JSON and hashing library

## Context

All hashes use SHA-256 over RFC 8785 JCS; hex lowercase; integers that may exceed 2^53 encoded as decimal strings.

## Goal

One shared, tested implementation of JCS + SHA-256 used by previews, evidence and consent.

## Scope

- In: JCS serializer for JsonNode/objects, Sha256Hex helper, guard that rejects raw large integers, RFC 8785 test vectors.
- Out: Evidence chain logic itself.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] All RFC 8785 appendix test vectors pass
- [ ] Key ordering, number and unicode escaping match the spec
- [ ] Large integer passed as number throws; as string passes
- [ ] Public API documented in XML comments

## Verification

dotnet test --filter Canonical

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:21 · user · blocked → done · PR #64 merged into main on GitHub (board synced after the merge)
