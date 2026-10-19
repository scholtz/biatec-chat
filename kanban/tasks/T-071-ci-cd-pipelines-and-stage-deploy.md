---
id: T-071
title: CI/CD pipelines and stage deploy
status: in-progress
priority: P1
epic: "Deployment"
blocked_by: [T-054]
area: [".github/workflows/ci.yml", ".github/workflows/deploy-stage.yml", "docs/cicd.md", "k8s/base/backend.yaml", "k8s/base/builders.yaml", "k8s/main/kustomization.yaml", "k8s/stage/kustomization.yaml", "k8s/README.md"]
parallel: yes
conflicts_with: []
issue: "#128"
branch: "task/T-071"
owner: "session-20261020-claude"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#non-functional-requirements"
created: 2026-10-20
updated: 2026-10-20
---

# T-071 · CI/CD pipelines and stage deploy

## Context

Biatec Chat will run on Biatec's **stage** Kubernetes environment (`chat.stage.biatec.io`, namespace
`biatec-chat-stage`, overlay `k8s/stage` from T-054). There is no CI today: `.github/workflows` only holds
`kanban-sync.yml`. Scholtz provides the deployment credentials as **GitHub environment secrets** (environment
`Stage`), the same way BiatecDEX deploys (`BiatecDEX/.github/workflows/deploy-stage.yml`: Docker Hub
`DOCKERHUB_USER` / `DOCKERHUB_TOKEN`, a base64 `KUBE_CONFIG`, an immutable image tag, `kubectl rollout status`).

AWS access in the cluster uses the keyless web-identity role from `infra/aws/k8s-role.sh` (`aws-role-arn` key of
`biatec-chat-secrets`, see `k8s/README.md` "AWS access"). Never AWS access keys.

## Goal

Every PR is built and tested automatically, and every push to `main` deploys the tested backend and builders images
to stage, with all credentials coming from the `Stage` environment secrets.

## Scope

- In: `ci.yml` (PRs and `main`: backend build + tests, JS packages typecheck/test/build, builders build, Flutter
  analyze + test, Docker builds without push, `kubectl kustomize` render of both overlays); `deploy-stage.yml`
  (push to `main` + manual: tests, build and push both images with an immutable tag, sync `biatec-chat-secrets`
  from environment secrets, `kubectl apply -k` of `k8s/stage` with the pinned tag, rollout status); image names
  following Biatec's Docker Hub convention; `docs/cicd.md` with the exact secret list for Scholtz.
- Out: production promotion (follow-up, like BiatecDEX `promote-production.yml`); npm publishing of the widget;
  creating the AWS role and cluster OIDC issuer (`infra/aws/k8s-role.sh`, operator step); tenant seeding.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same files.

## Acceptance criteria

- [ ] `ci.yml` runs on pull requests and pushes to `main` and fails on any failing build or test.
- [ ] `deploy-stage.yml` deploys only from `main` (or manual dispatch), uses the `Stage` environment, pins an
      immutable image tag, and waits for `kubectl rollout status` of backend and builders.
- [ ] No credential appears in the repository; every secret is read from `secrets.*` and the kubeconfig is removed
      after the job.
- [ ] `docs/cicd.md` lists every secret Scholtz must set, with its format.
- [ ] Workflows pass `actionlint` (or an equivalent YAML/syntax check) and `kubectl kustomize` renders both
      overlays.

## Verification

`actionlint .github/workflows/*.yml`; `kubectl kustomize k8s/stage` and `k8s/main`; `dotnet test`, `pnpm -r test`
locally; first real run on GitHub once Scholtz has set the secrets.

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-20 00:44 · LukassSoukup · (new) → backlog · created on user request (stage deploy via GitHub Actions);
  blockers scan: T-054 (k8s manifests, done) is the only ordering need
- 2026-10-20 00:44 · session-20261020-claude · backlog → in-progress · claimed by LukassSoukup
