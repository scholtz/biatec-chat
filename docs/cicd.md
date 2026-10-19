# CI/CD

Two GitHub Actions workflows. Nothing in the repository holds a credential: deploy credentials are GitHub environment
secrets, and AWS access in the cluster is a keyless IAM role (see [k8s/README.md](../k8s/README.md#aws-access-no-keys)).

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml` | backend `dotnet test`; typecheck, test and build of `chat-protocol`, `chat-widget`, `builders`, `consent-protocol`, `mock-oidc`; Flutter analyze + test of the authenticator (non-blocking until T-072); Docker builds of both images (no push); `kubectl kustomize` of both overlays |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`; then, in the `Stage` environment: pushes `scholtz2/biatec-chat-backend` and `scholtz2/biatec-chat-builders` tagged `1.<yyyy.mm.dd>-<sha7>-beta`, optionally syncs `biatec-chat-secrets`, applies `k8s/stage` with that tag, waits for both rollouts |

Production promotion is not automated yet (follow the BiatecDEX `promote-production.yml` pattern: re-tag the verified
stage image, never rebuild).

## Secrets for the `Stage` environment

Set in GitHub → Settings → Environments → `Stage` (create it if missing; add required reviewers there if deploys
should be approved).

| Secret | Required | Value |
|---|---|---|
| `DOCKERHUB_USER` | yes | Docker Hub user that can push `scholtz2/biatec-chat-*` |
| `DOCKERHUB_TOKEN` | yes | Docker Hub access token (read/write) for that user |
| `KUBE_CONFIG` | yes | `base64 -w0` of a kubeconfig allowed to manage namespace `biatec-chat-stage` (deployments, services, ingress, config maps, secrets, service accounts; and create the namespace on the first run) |
| `POSTGRES_CONNECTION_STRING` | optional* | Npgsql connection string |
| `REDIS_CONNECTION_STRING` | optional* | StackExchange.Redis connection string |
| `KMS_KEY_ID` | optional* | KMS key id or ARN of the token-vault key (eu-central-1) |
| `OIDC_CLIENT_SECRET` | optional* | Biatec OIDC client secret of the `biatec-chat` tenant on stage OIDC |
| `BUILDERS_INTERNAL_SECRET` | optional* | Random shared secret between backend and builders (`openssl rand -hex 32`) |
| `AWS_ROLE_ARN` | optional* | Role from `infra/aws/k8s-role.sh`, e.g. `arn:aws:iam::<account>:role/biatec-chat-k8s-role` |

\* All six or none. When `POSTGRES_CONNECTION_STRING` is set, every deploy recreates `biatec-chat-secrets` in the
namespace from them and fails if any of the other five is missing. When it isn't set, the workflow leaves the
cluster's `biatec-chat-secrets` alone, so it can be managed out of band (sealed-secrets, external-secrets, `kubectl`).

## First deploy checklist

1. Secrets above set on the `Stage` environment.
2. DNS `chat.stage.biatec.io` points at the cluster ingress (cert-manager issues TLS through `letsencrypt-prod`).
3. AWS role created with `infra/aws/k8s-role.sh` for the cluster's service-account issuer (k8s/README.md "AWS access").
4. Run `deploy-stage` manually (Actions → deploy-stage → Run workflow) or merge to `main`.
5. Seed the `biatec-chat` tenant row (k8s/README.md "Tenants are not seeded outside Development").
