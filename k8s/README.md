# Kubernetes manifests

Kustomize overlays: `base/` (backend + builders sidecar), `main/` (chat.biatec.io) and `stage/` (stage.chat.biatec.io).
Not applied by this repository.

Validate offline: `kubectl kustomize k8s/stage` and `kubectl kustomize k8s/main` must render. `kubectl apply --dry-run=client`
still needs a reachable API server for discovery (even with `--validate=false`), so run it against a kind cluster.

## Environment-specific endpoints

`base/` holds no environment endpoints. Each overlay sets them in `backend-config.patch.yaml` (ConfigMap:
`Oidc__Issuer`, `Oidc__McpResource`, `Mcp__Endpoint`, `Algod__Networks__*`) and `builders-env.patch.yaml` (`ALGOD_URL_*`).
`main` uses `oidc.biatec.io` and `mcp.biatec.io/tools`; `stage` uses `stage.oidc.biatec.io` and `stage.mcp.biatec.io/tools`
(per `docs/AWS-AGENTCORE-GUIDE.md`). Both use the public Nodely mainnet/testnet algod; change per overlay if stage needs other nodes.

## Dry-run status

`kubectl kustomize` renders both overlays offline (the substitute used when no cluster is reachable). The literal
`kubectl apply --dry-run=client` step needs an API server (for example a kind cluster) and was not run here; the cluster
owner should run `kubectl kustomize k8s/stage | kubectl apply --dry-run=client -f -`.

## Secrets (never in git)

Every secret is a GitHub environment secret; `deploy.yml` writes them into three cluster secrets on each deploy
(docs/cicd.md): `biatec-chat-secrets` (`oidc-client-secret`), `biatec-chat-generated` (`postgres-password`,
`redis-password`, `builders-internal-secret`, `vault-master-key`; the name is from an earlier design when they were
generated in the cluster) and `biatec-chat-aws` (Roles Anywhere client certificate, key and AWS config file, see "AWS
access"). Postgres and Redis run in the namespace (`base/postgres.yaml`, `base/redis.yaml`, reachable only from the
backend through network policies); the backend's connection strings are built from those passwords. Schema
migrations are a deploy step (docs/cicd.md).

## AWS access (Bedrock only, no keys)

AWS is used for Bedrock (model and guardrail) and nothing else. The backend authenticates with an environment-specific
client certificate through IAM Roles Anywhere: `AWS_CONFIG_FILE` points at the `config` of the `biatec-chat-aws` secret
(mounted at `/var/run/secrets/aws-rolesanywhere`), whose `credential_process` runs the `aws_signing_helper` shipped in
the backend image. This needs nothing from the cluster (no public service-account issuer, no API-server flag). One-time
setup with `infra/aws/rolesanywhere.sh`; the full walk-through is [docs/aws-setup.md](../docs/aws-setup.md).

## Images

`scholtz2/biatec-chat-backend` (`backend/Dockerfile`) and `scholtz2/biatec-chat-builders`
(`builders/sidecar/Dockerfile`); both build with the repository root as context. Startup and readiness probes use `/health` (Redis and PostgreSQL checks); liveness uses `/health/live`, which runs no dependency checks, so a dependency outage removes pods from service without restarting them.

## Deployment prerequisites

- **Image tags.** The `newTag` values in the overlays are placeholders. `.github/workflows/deploy.yml` replaces both with
  the immutable tag it deploys: `1.<date>-<sha7>-beta` on stage (deploy-stage.yml), `1.<date>-<sha7>-main` on production
  (promote-production.yml, the same images re-tagged). Applying an overlay by hand without pinning fails on purpose
  (`REPLACE-WITH-RELEASE-SHA` doesn't exist). See `docs/cicd.md`.
- **Tenants are not seeded by the backend outside Development.** `deploy.yml` upserts the `biatec-chat` tenant row after
  the migration: host from the environment URL, client id from the `OIDC_CLIENT_ID` secret,
  `ClientSecretRef=secret://biatec-chat/oidc-client-secret`.
  The secret itself is the `oidc-client-secret` key above, exposed as `Secrets__biatec-chat__oidc-client-secret`.
- **Vault** uses the `Secret` key provider: `Vault__MasterKey` from `biatec-chat-generated` wraps the per-entry data keys
  (AES-GCM). Losing that key only forces users to sign in again.
- **Evidence signing keys** have no production store yet. `Evidence__AllowEphemeralKeys=true` is set knowingly: keys are
  in-memory, lost on restart and differ between replicas (so the JWKS may be inconsistent across the 2 replicas) until a persistent
  `IEvidenceKeyStore` is registered.
- The backend runs as numeric UID 1654 (`runAsUser` plus `USER 1654` in the Dockerfile).
