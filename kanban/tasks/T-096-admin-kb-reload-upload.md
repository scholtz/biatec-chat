---
id: T-096
title: "Admin: knowledge-base reload and upload"
status: done
priority: P3
epic: "Orchestrator"
blocked_by: [T-086]
area: ["backend/Biatec.Chat.Tools/Kb/**", "backend/Biatec.Chat.Api/Chat/SignIn/**", "backend/Biatec.Chat.Api/Admin/**", "backend/Biatec.Chat.Tests/Kb/**"]
parallel: no
conflicts_with: [T-097]
issue: "#191"
branch: "task/T-096"
owner: "worker-T-096"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-096 · Admin: knowledge-base reload and upload

## Context

Deferred half of T-086 (spec PRODUCT-DESCRIPTION.md:302, "an admin API for policy and knowledge-base management"). `KbSearchService` is an immutable singleton loaded once from `Kb:Path` (Tools/Kb/KbSearchService.cs:24-26; registered in Chat/SignIn/AnonymousTierExecutors.cs:76-81), and a file uploaded to one replica is not seen by the others.

## Goal

Operators can reload and upload knowledge-base pages, and every replica serves the same pages.

## Scope

- In: a reloadable KbSearchService; shared KB storage (a Postgres table, or S3 in eu-central-1 tagged Project=biatec); operator endpoints to upload, list, delete and reload pages (same OperatorAuth and audit as T-086); size and type limits; pages stay untrusted tool-result data.
- Out: a KB editor UI.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-097: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [x] An uploaded page is searchable on every replica after reload (test)
- [x] Uploads over the size limit or of a disallowed type are refused (test)

## Verification

`dotnet test --filter Kb|Admin`, then the full backend suite.

## Hand-off

- **Summary:** uploaded KB pages in a PostgreSQL `KbPages` table (migration; no AWS resource) over the `Kb:Path` seed; `KbSearchService` swaps an immutable page set atomically; `KbLibrary` reloads on the replica that took the change and on every replica via a `Kb:RefreshSeconds` (30 s) id+hash check, keeping the current pages (and serving a new seed) during a DB outage. Operator endpoints under `/onboarding/operator/kb/` (`GET /pages` with served vs uploaded, `PUT /pages/{id}` text/markdown or text/plain UTF-8 ≤ 16 KiB, reason in `X-Admin-Reason`, `DELETE /pages/{id}`, `POST /reload` → 503 partial on DB failure) with T-086's auth, rate limit and audit style; writes and deletes share one advisory lock; ids `\A…\z` ≤ 64; audits written after commit even if this replica's reload fails. Pages reach the model only as untrusted `kb.search` results.
- **Branch:** `task/T-096` @ `09bfb48`, PR #196 (`Closes #191`).
- **Files outside the area:** `Core/Data/Entities.cs`, `ChatDbContext.cs`, migration `KbPages` + snapshot, `docs/admin-api.md`.
- **Verification:** Kb + Admin 64 passed; full backend on current main 1222 passed, 1 skipped, 0 failed. Review (medium): all realistic findings fixed.
- **Known limitations:** duplicated Content-Type/size checks in endpoint and service; duplicate seed file names keep the first (logged); the KbPages migration must be applied as a deploy step outside Development.
- **Follow-up proposed:** knowledge-base editor UI and page version history.
- **Unblocks:** T-097 (conflict) can start once this leaves in-progress.

## Review

- 2026-10-20 06:06 · approve · PR #196 merged into main @ 7cc6302 (recorded by kanban-sync)

## Activity log

- 2026-10-20 05:31 · coordinator · (new) → backlog · proposed by a worker / found during verification
- 2026-10-20 05:31 · coordinator · backlog · conflicts_with + T-097 (blockers scan: shared files)
- 2026-10-20 05:32 · LukassSoukup · taken · status stays backlog
- 2026-10-20 05:32 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 06:05 · coordinator · in-progress → review · branch task/T-096 @ 09bfb48, PR #196
- 2026-10-20 06:06 · user · review → done · PR #196 merged into main @ 7cc6302 (synced by kanban-sync)
