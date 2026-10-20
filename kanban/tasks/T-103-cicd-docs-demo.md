---
id: T-103
title: "docs/cicd.md: document the demo image and pipeline"
status: review
priority: P3
epic: "Deploy & docs"
blocked_by: []
area: ["docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#212"
branch: "task/T-103"
owner: "worker-T-103"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-103 · docs/cicd.md: document the demo image and pipeline

## Context

From T-100: the stage pipeline now builds the demo image (`STAGE_URL` feeds its BACKEND_URL build-arg and the deploy url), and deploy.yml detects the demo from the rendered overlay, waits for its rollout and registers its origin and client key on the stage tenant. docs/cicd.md doesn't describe any of it.

## Goal

docs/cicd.md describes the demo image, the overlay-driven tenant step and STAGE_URL.

## Scope

- In: docs only.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] docs/cicd.md covers the demo image, STAGE_URL and the tenant step

## Verification

Read against .github/workflows/deploy-stage.yml and deploy.yml.

## Hand-off

- **Summary:** docs/cicd.md documents the demo image, STAGE_URL, the overlay-driven demo detection, the tenant registration step and the rollout wait; fixed the split Secrets table and the namespace wording. Cross-checked against ci.yml, deploy-stage.yml, deploy.yml, promote-production.yml, apps/demo/Dockerfile, k8s overlays.
- **Branch:** `task/T-103`, PR #214 (`Closes #212`).
- **Unblocks:** no task is blocked by T-103.

## Review

## Activity log

- 2026-10-20 07:59 · coordinator · (new) → backlog · proposed by a worker
- 2026-10-20 07:59 · LukassSoukup · taken · status stays backlog
- 2026-10-20 07:59 · coordinator · backlog → in-progress · dispatched to worker
- 2026-10-20 08:02 · coordinator · in-progress → review · PR #214
