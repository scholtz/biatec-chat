---
id: T-106
title: "Docs consistency audit: make every doc match the code on main"
status: done
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

- [x] docs/architecture.md's build status reflects main (nothing listed as not built that is built, and vice versa)
- [x] Every endpoint, config key, workflow and path named in the docs exists as described (spot-checked list in the hand-off)
- [x] No doc refers to a done task as future work
- [x] All relative links resolve
- [x] Spec-vs-implementation differences listed in the hand-off

## Verification

A link checker over the changed docs; grep checks for every endpoint / config key the docs name.

## Hand-off

task/T-106 @ 32dc459, PR #223. Docs only: 23 Markdown files corrected against main (architecture build status, secrets,
ci contents, threat model, demo status, endpoints, links). Link check: 0 broken across 46 docs; 58 config keys and all
named endpoints/paths grep-verified. EVIDENCE_MASTER_KEY is documented as deploy-synced, which depends on PR #222.
Spec-vs-implementation differences are listed in the PR body. Follow-ups: anchoring resume/end + bundle proofs,
authenticator default mock URL, conformance suite in CI, k8s/main storage.yaml, fiat price source.

**Unblocks:** none.

## Review

- 2026-10-20 09:19 · approve · PR #223 merged into main @ c5efedf (recorded by kanban-sync)

## Activity log

- 2026-10-20 08:47 · coordinator · (new) → in-progress · user request: check docs for inconsistencies across the repo
- 2026-10-20 09:04 · coordinator · in-progress → review · PR #223
- 2026-10-20 09:19 · user · review → done · PR #223 merged into main @ c5efedf (synced by kanban-sync)
