---
id: T-008
title: Encrypted token vault in Redis
status: done
priority: P0
epic: "Sessions & auth"
blocked_by: [T-002]
area: ["backend/Biatec.Chat.Api/Vault/**"]
parallel: yes
conflicts_with: []
issue: "#9"
branch: "task/T-008"
owner: ""
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#rules"
created: 2026-10-19
updated: 2026-10-19
---

# T-008 · Encrypted token vault in Redis

## Context

Tokens encrypted at rest with a KMS key, stored in Redis keyed by session id, TTL = absolute lifetime + 1 h; refresh single-flighted with a Redis lock; never logged.

## Goal

Vault service storing access, refresh and id tokens safely.

## Scope

- In: IKeyProvider with AWS KMS envelope encryption (eu-central-1, default credential chain) and a local dev key provider; TokenVaultEntry in Redis; refresh-on-demand with distributed lock and rotation; log redaction.
- Out: OAuth flows themselves.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Stored values are ciphertext in Redis
- [ ] Concurrent refresh from two callers performs one refresh
- [ ] TTL set to absolute+1h
- [ ] Dev provider works without AWS; KMS provider has an integration test behind a flag

## Implementation notes

A customer-managed KMS key bills while idle (about USD 1 per month). Don't create it from this task; the dev key provider is the default, and the key is provisioned by T-055's scripts only after the user approves. Tag it `Project=biatec`, region `eu-central-1`.

## Verification

dotnet test --filter Vault

## Hand-off

## Review

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 18:00 · coordinator · review · added note: KMS key bills while idle, needs user approval (AGENTS.md rule)
- 2026-10-19 18:23 · kanban-board · blocked → backlog · blockers done: T-002
- 2026-10-19 18:27 · user · backlog → done · PR #67 merged into main @ 3589a9a (synced by kanban-board)
