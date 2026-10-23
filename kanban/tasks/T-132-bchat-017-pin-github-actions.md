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

- [x] No `uses:` of a third-party action references a tag; each is `owner/repo@<40 hex> # vN`.
- [x] Local reusable workflows (`./.github/workflows/*.yml`) are unchanged.
- [x] `.github/dependabot.yml` exists for `github-actions`.
- [x] CI on the PR still passes with the pinned actions; TEST-014 passes.

## Verification

`BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-014.sh`; the PR's own CI run.

## Hand-off

- **Summary:** All 35 third-party action references in the six workflows are pinned to the commit SHA of the release their tag pointed to (annotated tags dereferenced), each with the exact release as a comment (pnpm/action-setup has no matching semver tag, so it says v4); .github/dependabot.yml proposes grouped weekly updates after a 7 day cooldown. PR #316.
- **Files changed:** .github/workflows/*.yml, .github/dependabot.yml.
- **How it was verified:** TEST-014 passes; the PR's own CI run (all jobs) succeeded with the pinned actions.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** I resolved each SHA from the tag through the GitHub API today; I did not audit the action source at those commits. azure/setup-kubectl and pnpm/action-setup still resolve a tool version at run time (kubectl latest, pnpm from package.json) and the downloads are not verified, so the deploy job is only partly hardened; pinning those versions is a separate change. Dependabot version updates must be enabled for the repository to open PRs.

## Review

## Activity log

- 2026-10-23 14:00 · claude · (new) → backlog · created from GitHub issue #270 (BCHAT-017); blockers scan: none
- 2026-10-23 11:07 · scholtz · taken, and session-20261023-1107 · backlog → in-progress · claimed
