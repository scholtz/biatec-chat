# Biatec Chat

An embeddable Vue 3 chat widget, an ASP.NET Core 10 backend-for-frontend that calls Amazon Bedrock, and a Flutter
authenticator. The specification is [PRODUCT-DESCRIPTION.md](PRODUCT-DESCRIPTION.md).

## Layout

| Path | Contents |
|---|---|
| `packages/chat-widget` | `@biatec/chat-widget` Vue 3 library, shown to users as Biatec AI; UI rules in its `AGENTS.md` |
| `packages/chat-protocol` | Shared TypeScript types generated from the backend OpenAPI |
| `packages/consent-protocol` | Biatec Consent Protocol spec, schemas, conformance tests |
| `backend/` | `Biatec.Chat.Api`, `.Core`, `.Tools`, `.Tests` (solution `Biatec.Chat.slnx`) |
| `apps/biatec_authenticator` | Flutter app |
| `docs/`, `k8s/` | Documentation and deployment manifests; the widget design system is in `docs/design/` |

## Getting started

```sh
pnpm install
dotnet build backend/Biatec.Chat.slnx
dotnet test backend/Biatec.Chat.slnx
docker compose up -d   # redis:7 and postgres:16
```

To run the whole app locally (dependencies, backend, widget playground) use `./scripts/run.sh`; see [runGuide.md](runGuide.md).

Work is tracked on the board in [kanban/](kanban/README.md).
