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
`postgres-connection-string`, `redis-connection-string`, `kms-key-id`, `oidc-client-secret`, `builders-internal-secret`.
AWS access for Bedrock and KMS (eu-central-1) comes from an IAM role bound to the `biatec-chat-backend` service account;
no access keys are used.

## Images

`ghcr.io/scholtz/biatec-chat-backend` (`backend/Dockerfile`) and `ghcr.io/scholtz/biatec-chat-builders`
(`builders/sidecar/Dockerfile`); both build with the repository root as context. Probes use `/health`.

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
