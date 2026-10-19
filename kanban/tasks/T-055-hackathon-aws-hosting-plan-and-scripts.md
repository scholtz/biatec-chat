---
id: T-055
title: Hackathon AWS hosting plan and scripts
status: done
priority: P1
epic: "Deploy & docs"
blocked_by: [T-054]
area: ["infra/aws/**", "docs/deploy-aws.md"]
parallel: yes
conflicts_with: []
issue: "#56"
branch: "task/T-055"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#chat-backend-biatecchatapi"
created: 2026-10-19
updated: 2026-10-19
---

# T-055 · Hackathon AWS hosting plan and scripts

## Context

Bedrock credentials must come from an IAM role, never keys; cheapest option that does not bill while idle is preferred; anything idle-billing needs user approval.

## Goal

A ready plan the user can approve and run for the demo.

## Scope

- In: Doc comparing options (local backend with SSO profile for demo; EC2 t4g.small with instance role running compose), IAM role policy scoped to the eu. model ids and KMS key, scripts that create but are not executed without user approval, teardown script, tags Project=biatec.
- Out: Provisioning anything.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] Doc lists cost per day per option
- [ ] Role policy scoped to listed model ids and KMS key
- [ ] Teardown script deletes everything tagged
- [ ] Scripts default to dry-run

## Verification

Review doc; bash -n infra/aws/*.sh

## Hand-off

## Review

- 2026-10-19 22:28 · approve · PR #97 merged into main @ f356bbd (recorded by kanban-sync)

## Activity log

- 2026-10-19 17:30 · kanban-split · (new) → blocked · created from PRODUCT-DESCRIPTION.md
- 2026-10-19 21:21 · kanban-board · blocked → backlog · blockers done: T-054
- 2026-10-19 21:24 · scholtz · taken · status stays backlog
- 2026-10-19 21:24 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
- 2026-10-19 22:28 · user · in-progress → done · PR #97 merged into main @ f356bbd (synced by kanban-sync)
