---
id: T-132
title: "Pin GitHub Actions to commit SHAs and add Dependabot (BCHAT-017)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: [".github/workflows/ci.yml", ".github/workflows/deploy.yml", ".github/workflows/deploy-stage.yml", ".github/workflows/promote-production.yml", ".github/workflows/publish-widget.yml", ".github/workflows/kanban-sync.yml", ".github/dependabot.yml"]
parallel: yes
conflicts_with: []
issue: "#270"
branch: "task/T-132"
owner: "session-20261023-1107"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-132 - Pin GitHub Actions to commit SHAs and add Dependabot (BCHAT-017)

## Context

Security audit finding BCHAT-017 (Low, issue #270). Every workflow references third-party actions by mutable tag (`@v4`), including
jobs with `id-token: write` and deploy secrets. A moved or compromised tag runs attacker code with those credentials.

## Goal

Every third-party action is pinned to the full commit SHA of the release the tag points to today, with the tag kept as a comment, and
Dependabot proposes updates.

## Scope

- In: SHA pins in all workflows; `.github/dependabot.yml` for the `github-actions` ecosystem.
- Out: image signing and provenance (BCHAT-018), base-image digests.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] No `uses:` of a third-party action references a tag; each is `owner/repo@<40 hex> # vN`.
- [ ] Local reusable workflows (`./.github/workflows/*.yml`) are unchanged.
- [ ] `.github/dependabot.yml` exists for `github-actions`.
- [ ] CI on the PR still passes with the pinned actions; TEST-014 passes.

## Verification

`BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-014.sh`; the PR's own CI run.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 14:00 · claude · (new) → backlog · created from GitHub issue #270 (BCHAT-017); blockers scan: none
- 2026-10-23 11:07 · scholtz · taken, and session-20261023-1107 · backlog → in-progress · claimed
