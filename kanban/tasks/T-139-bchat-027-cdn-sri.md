---
id: T-139
title: "Show Subresource Integrity in the CDN embedding and publish the hash with each release (BCHAT-027)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/chat-widget/README.md", "docs/integration-widget.md", ".github/workflows/publish-widget.yml", "packages/chat-widget/test/docs-sri.test.ts"]
parallel: yes
conflicts_with: []
issue: "#280"
branch: "task/T-139"
owner: "session-20261023-1246"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-139 - Show Subresource Integrity in the CDN embedding and publish the hash with each release (BCHAT-027)

## Context

Security audit finding BCHAT-027 (Low, issue #280). The documented one-line embedding loads the widget from a CDN with no
`integrity` attribute. A compromised CDN or package would run attacker code in every embedding page, which is the origin that holds
the visitor's session context for that site.

## Goal

The documented snippet carries `integrity` and `crossorigin`, with the real hash of the pinned version, and every release publishes the
hash of its standalone file.

## Scope

- In: `integrity="sha384-..."` and `crossorigin="anonymous"` in the README and `docs/integration-widget.md` snippets for 0.1.0, with how to compute the hash for another version; the release job adds the hash to the GitHub release notes; a test that keeps the docs honest.
- Out: self-hosting guidance beyond a sentence; npm provenance (already on).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Both snippets show `integrity="sha384-<base64>"` and `crossorigin="anonymous"` and name the pinned version.
- [x] The hash in the docs equals the SHA-384 of the file jsDelivr serves for that version (checked when the change was made).
- [x] The release workflow computes the hash from the published tarball and writes it to the release notes.
- [x] A test fails if a snippet drops `integrity`/`crossorigin` or pins a version other than `package.json`'s.

## Verification

`pnpm --filter @biatec/chat-widget test`; the shell step run against a locally packed tarball.

## Hand-off

- **Summary:** The documented CDN snippets (package README and docs/integration-widget.md) carry integrity="sha384-..." and crossorigin="anonymous" with the real hash of 0.1.0 and how to compute it for another version. The release job downloads the published tarball, extracts the standalone file (so a tar failure stops the step), checks it is non-empty, hashes it with SHA-384, and writes the value to the notes of a new release, or edits an existing release that lacks it, and to the job summary. docs-sri.test.ts keeps the snippets honest.
- **Files changed:** packages/chat-widget/README.md, docs/integration-widget.md, .github/workflows/publish-widget.yml, packages/chat-widget/test/docs-sri.test.ts.
- **How it was verified:** The hash in the docs equals the SHA-384 of the file jsDelivr serves for @biatec/chat-widget@0.1.0 (downloaded and hashed); the workflow's extract-and-hash commands run against a tarball built from that file give the same value; widget suite 434 passed, 0 failed; TEST-014 passes.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The workflow change only runs at the next publish (or a re-run), so it is exercised locally but not in CI. The hash comes from the CI-built tarball; if a version was already on npm from another build, the two could differ, and nothing compares them. The snippets are not tied to package.json's version (a release PR cannot know the hash of a file that does not exist yet), so a version bump needs a follow-up that copies the hash from the release notes.

## Review

## Activity log

- 2026-10-23 21:00 · claude · (new) → backlog · created from GitHub issue #280 (BCHAT-027); blockers scan: none
- 2026-10-23 12:46 · scholtz · taken, and session-20261023-1246 · backlog → in-progress · claimed
