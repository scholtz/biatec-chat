# Run guide

How to run Biatec Chat on a developer machine. One command starts everything:

```sh
./scripts/run.sh
```

Then open <http://localhost:5173>; the chat launcher is at the bottom right. Ctrl+C stops all processes (Redis and Postgres
keep running in docker).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10 | The script also finds it in `~/.dotnet` when it is not on `PATH` |
| Node | 22+ (22.18+ for `--with-mock-oidc`) | |
| pnpm | 10 | `corepack enable` |
| Docker | with `docker compose` | Redis 7 and Postgres 16 |
| AWS CLI | v2 | Only for Bedrock (chat answers) and KMS |

AWS: sign in to the hackathon SSO profile before starting, or chat answers fail (everything else still works):

```sh
aws sso login --profile hackathon
```

The script sets `AWS_PROFILE=hackathon` and `AWS_REGION=eu-central-1` unless you set them yourself. Never put access keys
in files or environment variables; see [AGENTS.md](AGENTS.md).

## What starts

| Service | URL | Started by default |
|---|---|---|
| Redis, Postgres | `127.0.0.1:6379`, `127.0.0.1:5432` | yes (docker compose project `biatec-chat`) |
| Backend (`Biatec.Chat.Api`) | <http://localhost:5000>, health at `/health` | yes |
| Widget playground | <http://localhost:5173> | yes; skip with `--backend-only` |
| Builders sidecar | <http://127.0.0.1:3100> | `--with-builders` |
| Mock Biatec OIDC (consent side) | <http://127.0.0.1:8089> | `--with-mock-oidc` |

The backend runs as `Development`: on startup it applies database migrations and seeds the `localhost` tenant, which
allows the origins `http://localhost:5173` and `http://localhost:3000`. That is why the playground port is fixed.

## Options

```text
--with-builders   also start the builders sidecar on :3100 (CLAMM and algo-safe transaction groups)
--with-mock-oidc  also start the mock Biatec OIDC on :8089 (consent side; needs Node >= 22.18)
--backend-only    start dependencies and the backend, not the widget playground
--skip-install    skip pnpm install and the chat-protocol build
```

Environment overrides: any backend setting can be set with ASP.NET Core double-underscore variables, for example
`Postgres__ConnectionString=...` to use a different database. `BUILDERS_INTERNAL_SECRET` overrides the local-only
sidecar secret, and `COMPOSE_PROJECT_NAME` the docker compose project.

## Running pieces by hand

```sh
docker compose -p biatec-chat up -d redis postgres
pnpm install && pnpm -C packages/chat-protocol build
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5000 dotnet run --project backend/Biatec.Chat.Api
pnpm -C packages/chat-widget dev --port 5173 --strictPort
```

## Tests

```sh
dotnet test backend/Biatec.Chat.slnx                 # about 6 minutes
pnpm -C packages/chat-protocol build                 # needed before the widget typecheck
pnpm -C packages/chat-widget typecheck && pnpm -C packages/chat-widget test
pnpm -C packages/consent-protocol test && pnpm -C packages/consent-protocol conformance
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Bind for 127.0.0.1:6379 failed: port is already allocated` | Another compose project owns the ports. Use the default project name `biatec-chat`, or stop the other one |
| Widget typecheck: `Cannot find module '@biatec/chat-protocol'` | `pnpm -C packages/chat-protocol build` |
| Chat answers fail, backend log shows credential errors | `aws sso login --profile hackathon` (sessions last 12 hours) |
| `ValidationException` from Bedrock | Model IDs need the `eu.` prefix, and only Claude 4.5 models work on this account |
| `Port 5173 is in use` | Stop the other Vite server; the port must stay 5173 for CORS |
| Mock OIDC fails to start | Needs Node 22.18 or later (runs TypeScript directly) |

Not covered here: the Flutter authenticator (`apps/biatec_authenticator`, needs the Flutter SDK) and AWS deployment
([docs/deploy-aws.md](docs/deploy-aws.md)).
