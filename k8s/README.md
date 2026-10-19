# Kubernetes manifests

Kustomize overlays: `base/` (backend + builders sidecar), `main/` (chat.biatec.io) and `stage/` (chat.stage.biatec.io).
Not applied by this repository.

Validate: `kubectl kustomize k8s/stage | kubectl apply --dry-run=client -f -` (the same for `k8s/main`).

## Secrets (never in git)

Create `biatec-chat-secrets` in each namespace out of band (sealed-secrets, external-secrets or `kubectl create secret`) with keys:
`postgres-connection-string`, `redis-connection-string`, `kms-key-id`, `builders-internal-secret`.
AWS access for Bedrock and KMS (eu-central-1) comes from an IAM role bound to the `biatec-chat-backend` service account;
no access keys are used.

## Images

`ghcr.io/scholtz/biatec-chat-backend` (`backend/Dockerfile`) and `ghcr.io/scholtz/biatec-chat-builders`
(`builders/sidecar/Dockerfile`); both build with the repository root as context. Probes use `/health`.
