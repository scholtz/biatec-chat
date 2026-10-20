---
id: T-105
title: "npm pipeline: release @biatec/chat-widget automatically on a version bump"
status: in-progress
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

- [ ] A push to main with a new version publishes and tags it (verified by workflow logic review + actionlint; a dry
      run path that prints what it would do)
- [ ] A push to main without a version change, or with an already published version, publishes nothing
- [ ] The tag created by the workflow does not publish twice
- [ ] README "Publishing" and docs/cicd.md describe the automatic release, the secret and the environment

## Verification

actionlint; `act` or a dry-run walkthrough of each path; read the docs against the workflow.

## Hand-off

## Review

## Activity log

- 2026-10-20 08:10 · coordinator · (new) → in-progress · user request: npm package needs its own CI/CD pipeline that uploads to npm
