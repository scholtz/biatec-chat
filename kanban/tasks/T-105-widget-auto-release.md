---
id: T-105
title: "npm pipeline: release @biatec/chat-widget automatically on a version bump"
status: review
priority: P2
epic: "Deploy & docs"
blocked_by: []
area: [".github/workflows/publish-widget.yml", "packages/chat-widget/README.md", "docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#217"
branch: "task/T-105"
owner: "worker-T-105"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#chat-widget-biatecchat-widget"
created: 2026-10-20
updated: 2026-10-20
---

# T-105 · npm pipeline: release @biatec/chat-widget automatically on a version bump

## Context

User request: the npm package needs its own CI/CD pipeline that uploads to npm. T-099 added
`.github/workflows/publish-widget.yml` (PR dry runs; publish on a `chat-widget-v<version>` tag or a manual run, only
for commits on main; npm trusted publishing with the `NPM_TOKEN` environment secret of environment `npm` as fallback),
but a release still needs someone to push the tag by hand.

## Goal

Merging a version bump of `packages/chat-widget/package.json` to main publishes that version to npm and tags it,
with no manual step; nothing is published when the version didn't change or is already on npm.

## Scope

- In: a `push` to `main` trigger (paths: the widget and chat-protocol packages, the workflow); a check that the
  package.json version is not yet on npm (`npm view @biatec/chat-widget@<version> version`, treating "not found" /
  first publish correctly); if new: build, test, pack and publish exactly as the tag path does (same environment
  `npm`, same auth), then create and push the `chat-widget-v<version>` tag (contents: write only for that step) and a
  GitHub release with the CHANGELOG section; if already published or unchanged: skip with a notice. Keep the tag and
  manual paths, keep PR dry runs. Make the tag push not re-trigger a second publish (the tag path must see the version
  is already published and skip). Update packages/chat-widget/README.md "Publishing" and docs/cicd.md so they state
  exactly how releasing works now and which secret/environment is used.
- Out: publishing chat-protocol; changing the package.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] A push to main with a new version publishes and tags it (verified by workflow logic review + actionlint; a dry
      run path that prints what it would do)
- [x] A push to main without a version change, or with an already published version, publishes nothing
- [x] The tag created by the workflow does not publish twice
- [x] README "Publishing" and docs/cicd.md describe the automatic release, the secret and the environment

## Verification

actionlint; `act` or a dry-run walkthrough of each path; read the docs against the workflow.

## Hand-off

- **Summary:** `publish-widget.yml` is the widget's own release pipeline: check (version, changed?, on npm?, semver gate: only versions above the highest published release), pack (build, typecheck, test, pack; publish dry run only when the version isn't on npm), publish (environment `npm`, trusted publishing + provenance, `NPM_TOKEN` environment-secret fallback, main-only guard, npm re-check right before publishing, "already published" treated as a skip; prereleases go to their own dist-tag, never `latest`), release (tag `chat-widget-v<version>` + GitHub release from the CHANGELOG, prerelease-marked). Bump the version + CHANGELOG in a PR to main → merge → published, tagged, released. Tag and manual paths kept.
- **Branch:** `task/T-105` @ `5ea1d70`, PR #219 (`Closes #217`).
- **Verification:** actionlint clean; PR #219's own publish-widget run green (check + pack; publish/release skipped as expected); CI green; decide and publish steps simulated for every path with the real npm state (0.0.0-stage, 0.1.0). Review (medium): all findings fixed.
- **State:** scholtz published 0.1.0 (and 0.0.0-stage) by a manual run of the earlier workflow; 0.1.0 has no git tag or GitHub release (optional, by hand).
- **Unblocks:** no task is blocked by T-105.

## Review

## Activity log

- 2026-10-20 08:10 · coordinator · (new) → in-progress · user request: npm package needs its own CI/CD pipeline that uploads to npm
- 2026-10-20 08:33 · coordinator · in-progress → review · PR #219
