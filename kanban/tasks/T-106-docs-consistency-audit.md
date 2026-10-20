---
id: T-106
title: "Docs consistency audit: make every doc match the code on main"
status: in-progress
priority: P1
epic: "Deploy & docs"
blocked_by: []
area: ["docs/**", "README.md", "runGuide.md", "packages/*/README.md", "apps/*/README.md", "backend/**/README.md", "builders/**/README.md", "apps/biatec_authenticator/README.md"]
parallel: yes
conflicts_with: []
issue: "#221"
branch: "task/T-106"
owner: "worker-T-106"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md"
created: 2026-10-20
updated: 2026-10-20
---

# T-106 · Docs consistency audit: make every doc match the code on main

## Context

User request (2026-10-20): the docs must be checked for inconsistencies across the repo. Example: docs/architecture.md
"What is not built yet" still lists the orchestrator, SSE chat endpoint, confirm-and-sign, wallet/DEX/safe executors,
soft cap, consent orchestrator, widget chat UI and cards, the Biatec AI widget rework (T-062–T-069), authenticator
enrollment, evidence export, retention, x402, containers and Kubernetes — all of which are now built and merged.
Code freeze is today 13:30.

## Goal

Every statement in the repo's documentation is true for main today, and docs agree with each other.

## Scope

- In: every Markdown doc outside kanban/ and PRODUCT-DESCRIPTION.md (docs/**, root README.md / runGuide.md /
  juryQnA.md / AGENTS.md files only where they state facts about the code, package READMEs): build/"not built"
  status lists, component tables, endpoint paths and methods, config keys and defaults, env vars and secrets, workflow
  names and triggers, k8s/deploy facts, file paths, task references ("planned in T-0xx" for done tasks), feature flags,
  links. Fix each wrong statement against the code (cite the code you checked). Keep wording style.
- In: a list of places where PRODUCT-DESCRIPTION.md and the implementation disagree — report only, do not edit the spec.
- Out: code changes; kanban/ files; the spec; AGENTS.md rules (only factual statements in them).

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [ ] docs/architecture.md's build status reflects main (nothing listed as not built that is built, and vice versa)
- [ ] Every endpoint, config key, workflow and path named in the docs exists as described (spot-checked list in the hand-off)
- [ ] No doc refers to a done task as future work
- [ ] All relative links resolve
- [ ] Spec-vs-implementation differences listed in the hand-off

## Verification

A link checker over the changed docs; grep checks for every endpoint / config key the docs name.

## Hand-off

## Review

## Activity log

- 2026-10-20 08:47 · coordinator · (new) → in-progress · user request: check docs for inconsistencies across the repo
