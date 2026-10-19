# Kubernetes manifests

Kustomize overlays: `base/` (backend + builders sidecar), `main/` (chat.biatec.io) and `stage/` (chat.stage.biatec.io).
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

Create `biatec-chat-secrets` in each namespace out of band (sealed-secrets, external-secrets or `kubectl create secret`) with keys:
`postgres-connection-string`, `redis-connection-string`, `kms-key-id`, `oidc-client-secret`, `builders-internal-secret`,
`aws-role-arn`.

## AWS access (no keys)

The pod gets a projected service-account token (audience `sts.amazonaws.com`) at `/var/run/secrets/aws/token`; the AWS SDK
exchanges it for the role in `AWS_ROLE_ARN` (`sts:AssumeRoleWithWebIdentity`, `AWSSDK.SecurityToken`). One-time setup per
cluster and AWS account, with `infra/aws/k8s-role.sh` (dry-run by default):

1. The cluster's service-account issuer must be reachable over HTTPS from AWS: `<issuer>/.well-known/openid-configuration`
   and its `jwks_uri` (`kubectl get --raw /.well-known/openid-configuration` shows the issuer). On a self-managed cluster
   that usually means setting `--service-account-issuer` to a public URL and serving those two documents there.
2. `ISSUER_URL=<issuer> KMS_KEY_ARN=<vault key arn> infra/aws/k8s-role.sh --apply` creates the IAM OIDC provider and the
   role `biatec-chat-k8s-role`, trusted only for `system:serviceaccount:biatec-chat-main:biatec-chat-backend` and
   `...-stage:...`, with the same inline policy as the EC2 role (Claude 4.5 `eu.` profiles, the guardrail, the vault key).
3. Put the printed role ARN in the `aws-role-arn` key of `biatec-chat-secrets`.

The hackathon AWS account turns read-only after the event; for a lasting deployment run the guardrail script and this one
in Biatec's own account and update `Bedrock__Guardrail__GuardrailId` in the base config map.

## Images

`ghcr.io/scholtz/biatec-chat-backend` (`backend/Dockerfile`) and `ghcr.io/scholtz/biatec-chat-builders`
(`builders/sidecar/Dockerfile`); both build with the repository root as context. Startup and readiness probes use `/health` (Redis and PostgreSQL checks); liveness uses `/health/live`, which runs no dependency checks, so a dependency outage removes pods from service without restarting them.

## Deployment prerequisites

- **Image tags.** `main` pins `newTag: REPLACE-WITH-RELEASE-SHA` on purpose: release automation must set an immutable tag
  (`kustomize edit set image ghcr.io/scholtz/biatec-chat-backend=...:<sha>`) before applying, so a deploy never floats on `latest`.
  `stage` follows the moving `stage` tag.
- **Tenants are not seeded outside Development.** After the first deploy, create the `biatec-chat` tenant row (host
  `chat.biatec.io`, `ClientSecretRef=secret://biatec-chat/oidc-client-secret`) via the onboarding API or SQL.
  The secret itself is the `oidc-client-secret` key above, exposed as `Secrets__biatec-chat__oidc-client-secret`.
- **Vault** uses KMS (`Vault__KeyProvider=Kms`, `Kms__KeyId`); the pod's IAM role needs `kms:GenerateDataKey` and `kms:Decrypt` on that key.
- **Evidence signing keys** have no production store yet. `Evidence__AllowEphemeralKeys=true` is set knowingly: keys are
  in-memory, lost on restart and differ between replicas (so the JWKS may be inconsistent across the 2 replicas) until a KMS-backed
  `IEvidenceKeyStore` is registered.
- The backend runs as numeric UID 1654 (`runAsUser` plus `USER 1654` in the Dockerfile).
