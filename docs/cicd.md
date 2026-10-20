# CI/CD

Biatec Chat runs on Biatec's own Kubernetes cluster in two environments. AWS is used for Bedrock only
([aws-setup.md](aws-setup.md)). Nothing in the repository holds a credential: deploy credentials are GitHub secrets.

| Environment | GitHub environment | Overlay | Namespace | Host | Image tag |
|---|---|---|---|---|---|
| stage | `Stage` | `k8s/stage` | `biatec-chat-stage` | stage.chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-beta` |
| production | `Production` | `k8s/main` | `biatec-chat-main` | chat.biatec.io | `1.<yyyy.mm.dd>-<sha7>-main` (same image) |

## Workflows

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | every pull request; called by `deploy-stage.yml` | backend `dotnet test` (with Postgres and Redis containers); typecheck, test and build of `chat-protocol`, `chat-widget`, `builders`, `consent-protocol`, `mock-oidc`; Flutter analyze + test of the authenticator; Docker builds of both images; `kubectl kustomize` of both overlays |
| [`deploy-stage.yml`](../.github/workflows/deploy-stage.yml) | push to `main` (except `kanban/**`, `docs/**`, `*.md`) and manual run | runs `ci.yml`, builds and pushes `scholtz2/biatec-chat-backend` and `scholtz2/biatec-chat-builders` as `<version>-beta` and the moving `stage` tag, then `deploy.yml` to `Stage` |
| [`promote-production.yml`](../.github/workflows/promote-production.yml) | manual only (Actions → promote-production → Run workflow) | takes a stage version (empty = what `stage` runs now), re-tags both images `<version>-beta` → `<version>-main` without rebuilding (same digest, checked), then `deploy.yml` to `Production` |
| [`deploy.yml`](../.github/workflows/deploy.yml) | called by the two above | in the given environment: ensures the namespace and the generated secrets, syncs `biatec-chat-secrets`, pins the tag in the overlay, `kubectl apply -k`, waits for Postgres and Redis, applies database migrations, waits for builders and backend |

Production is never deployed by a push. To approve production deploys, add required reviewers to the `Production`
environment (Settings → Environments → Production → Required reviewers); the deploy job then waits for approval.

## Secrets

Settings → Secrets and variables → Actions:

| Secret | Where | Value |
|---|---|---|
| `DOCKERHUB_USER` | repository | Docker Hub user that can push `scholtz2/biatec-chat-*` |
| `DOCKERHUB_TOKEN` | repository | Docker Hub access token (read/write) for that user |
| `KUBE_CONFIG` | `Stage` and `Production` environments, each its own | `base64 -w0` of a kubeconfig allowed to manage that environment's namespace: deployments, statefulsets, services, ingresses, network policies, config maps, secrets, service accounts, persistent volume claims, `pods/exec` (migration), and create the namespace on the first run |
| `OIDC_CLIENT_SECRET` | `Stage` and `Production` | Biatec OIDC client secret of the `biatec-chat` tenant at that environment's OIDC (stage.oidc.biatec.io / oidc.biatec.io) |
| `AWS_ROLE_ARN` | `Stage` and `Production` | Bedrock role from `infra/aws/rolesanywhere.sh` (aws-setup.md §3), e.g. `arn:aws:iam::<account>:role/biatec-chat-bedrock-role` |
| `AWS_TRUST_ANCHOR_ARN`, `AWS_PROFILE_ARN` | `Stage` and `Production` | IAM Roles Anywhere trust anchor and profile ARNs, printed by the same script |
| `AWS_CLIENT_CERT`, `AWS_CLIENT_KEY` | `Stage` and `Production`, each its own | PEM client certificate and private key for that environment (`rolesanywhere-out/stage/…` for `Stage`, `…/main/…` for `Production`); expire after 365 days, see aws-setup.md "Renewing" |

When `OIDC_CLIENT_SECRET` is set, every deploy recreates `biatec-chat-secrets` from it; when it isn't set, the cluster's
`biatec-chat-secrets` is left alone (managed out of band). The five `AWS_*` secrets are handled the same way for the
cluster secret `biatec-chat-aws` (certificate, key and the AWS config file): all five must be set, they are validated
(certificate matches key, expiry warning), and if none is set an existing cluster secret is kept; without either the
deploy stops with a pointer to aws-setup.md.

**Generated in the cluster, never in GitHub.** On the first deploy to a namespace, `deploy.yml` creates
`biatec-chat-generated` with random `postgres-password`, `redis-password`, `builders-internal-secret` and
`vault-master-key` (the token-vault key, `Vault__KeyProvider=Secret`); later deploys keep it and only add keys that
are missing. The backend builds its connection strings from those passwords and the in-cluster service names, so they
are configuration. Deleting `biatec-chat-generated` without the Postgres volume locks the backend out (Postgres keeps
the password it was initialised with); losing `vault-master-key` signs every user out.

## Database migrations

The backend migrates on startup only in Development. Each deploy runs
`dotnet ef migrations script --idempotent` and pipes it into `psql` in the Postgres pod, which applies just the
migrations missing from `__EFMigrationsHistory`. A migration must therefore be compatible with the previous backend
version for the few seconds both run.

## First deploy

1. AWS part: [aws-setup.md](aws-setup.md) §3 (guardrail exists; Roles Anywhere role, certificates and the five `AWS_*` secrets).
2. Secrets above, `Stage` first.
3. Merge to `main` or run `deploy-stage` manually; check https://stage.chat.biatec.io/health.
4. Seed the `biatec-chat` tenant row (k8s/README.md, "Tenants are not seeded outside Development").
5. When stage is verified: set the `Production` secrets, run `promote-production`, seed the tenant there too.
