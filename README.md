# Biatec Chat

An embeddable Vue 3 chat widget, an ASP.NET Core 10 backend-for-frontend that calls Amazon Bedrock, and a Flutter
authenticator. The specification is [PRODUCT-DESCRIPTION.md](PRODUCT-DESCRIPTION.md).

## Layout

| Path | Contents |
|---|---|
| `packages/chat-widget` | `@biatec/chat-widget` Vue 3 library, shown to users as Biatec AI; UI rules in its `AGENTS.md` |
| `packages/chat-protocol` | Shared TypeScript types generated from the backend OpenAPI |
| `packages/consent-protocol` | Biatec Consent Protocol spec, schemas, conformance tests, and the mock Biatec OIDC (`mock-oidc/`) |
| `packages/evidence-verifier` | Offline verifier for evidence export bundles |
| `backend/` | `Biatec.Chat.Api`, `.Core`, `.Tools`, `.Tests` (solution `Biatec.Chat.slnx`) |
| `builders/sidecar` | Node builder sidecar (CLAMM, algo-safe and x402 transaction groups) |
| `apps/biatec_authenticator` | Flutter app |
| `apps/demo` | Showcase demo site ([docs/demo-site.md](docs/demo-site.md)) |
| `e2e` | The demo script as an automated test against the mock OIDC |
| `infra/` | AWS scripts: Bedrock guardrail and IAM Roles Anywhere ([docs/aws-setup.md](docs/aws-setup.md)) |
| `docs/`, `k8s/` | Documentation and deployment manifests; the widget design system is in `docs/design/`, the map of the code in [docs/architecture.md](docs/architecture.md) |

## Getting started

```sh
pnpm install
dotnet build backend/Biatec.Chat.slnx
dotnet test backend/Biatec.Chat.slnx
docker compose up -d   # redis:7, postgres:16 and the builders sidecar
```

To run the whole app locally (dependencies, backend, widget playground) use `./scripts/run.sh`; see [runGuide.md](runGuide.md).

Work is tracked on the board in [kanban/](kanban/README.md).
