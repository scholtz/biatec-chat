---
id: T-071
title: CI/CD pipelines and stage deploy
status: review
priority: P1
epic: "Deployment"
blocked_by: [T-054]
area: [".github/workflows/ci.yml", ".github/workflows/deploy-stage.yml", "docs/cicd.md", "k8s/base/backend.yaml", "k8s/base/builders.yaml", "k8s/main/kustomization.yaml", "k8s/stage/kustomization.yaml", "k8s/README.md", "k8s/base/kustomization.yaml", "k8s/base/postgres.yaml", "k8s/base/redis.yaml", "docs/aws-setup.md", "docs/deploy-aws.md", "docs/AWS-AGENTCORE-GUIDE.md", "infra/aws/**", "infra/guardrail/**"]
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

- [x] `ci.yml` runs on pull requests and pushes to `main` and fails on any failing build or test.
- [x] `deploy-stage.yml` deploys only from `main` (or manual dispatch), uses the `Stage` environment, pins an
      immutable image tag, and waits for `kubectl rollout status` of backend and builders.
- [x] No credential appears in the repository; every secret is read from `secrets.*` and the kubeconfig is removed
      after the job.
- [x] `docs/cicd.md` lists every secret Scholtz must set, with its format.
- [x] Workflows pass `actionlint` (or an equivalent YAML/syntax check) and `kubectl kustomize` renders both
      overlays.

## Verification

`actionlint .github/workflows/*.yml`; `kubectl kustomize k8s/stage` and `k8s/main`; `dotnet test`, `pnpm -r test`
locally; first real run on GitHub once Scholtz has set the secrets.

## Hand-off

- **Summary:** `ci.yml` (PRs, and called by deploy) builds and tests backend, JS packages, builders, authenticator
  (non-blocking until T-072), both Docker images and both kustomize overlays. `deploy-stage.yml` (push to `main`,
  manual) runs CI, then in the `Stage` environment pushes `scholtz2/biatec-chat-{backend,builders}` with tag
  `1.<yyyy.mm.dd>-<sha7>-beta`, optionally recreates `biatec-chat-secrets` from environment secrets, applies
  `k8s/stage` with that tag and waits for both rollouts. Images moved from ghcr to Biatec's Docker Hub convention.
- **Files changed:** `.github/workflows/ci.yml`, `.github/workflows/deploy-stage.yml`, `docs/cicd.md`,
  `k8s/base/backend.yaml`, `k8s/base/builders.yaml`, `k8s/main/kustomization.yaml`, `k8s/stage/kustomization.yaml`,
  `k8s/README.md`.
- **How it was verified:** actionlint 1.7.7 clean; tag substitution + `kubectl kustomize k8s/stage` renders the
  pinned images; every CI step run locally from a clean `dist`; PR #130 CI on GitHub. The deploy job itself is
  unverified until Scholtz sets the `Stage` secrets.
- **Follow-up tasks created:** T-072 (two pre-existing failing Flutter enrollment tests).
- **Unblocks:** T-072 (ready once this is done).
- **Open questions:** PR #124 (keyless AWS role in the pod, `aws-role-arn`) should be merged before the first deploy.

## Review

## Activity log

Append-only. Format: `- YYYY-MM-DD HH:MM · <actor> · <from> → <to> · <note>`

- 2026-10-20 00:44 · LukassSoukup · (new) → backlog · created on user request (stage deploy via GitHub Actions);
  blockers scan: T-054 (k8s manifests, done) is the only ordering need
- 2026-10-20 00:44 · session-20261020-claude · backlog → in-progress · claimed by LukassSoukup
- 2026-10-20 00:49 · session-20261020-claude · in-progress → review · PR #130
- 2026-10-20 00:57 · session-20261020-claude · review → review · scope extended at the user's request: in-cluster Postgres/Redis with generated passwords, deploy-time migrations, AWS setup guide, merged fix/aws-access (#124)
