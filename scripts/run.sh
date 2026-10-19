#!/usr/bin/env bash
# Runs Biatec Chat locally: Redis + Postgres (docker compose), the ASP.NET Core backend on :5000 and the widget
# playground on :5173. Optional: the builders sidecar (:3100) and the mock Biatec OIDC (:8089). See runGuide.md.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/run.sh [options]

  --with-builders   also start the builders sidecar on :3100 (CLAMM and algo-safe transaction groups)
  --with-mock-oidc  also start the mock Biatec OIDC on :8089 (consent side; needs Node >= 22.18)
  --backend-only    start dependencies and the backend, not the widget playground
  --skip-install    skip pnpm install
  -h, --help        show this help
EOF
}

WITH_BUILDERS=0
WITH_MOCK_OIDC=0
WITH_WIDGET=1
INSTALL=1
for arg in "$@"; do
  case "$arg" in
    --with-builders) WITH_BUILDERS=1 ;;
    --with-mock-oidc) WITH_MOCK_OIDC=1 ;;
    --backend-only) WITH_WIDGET=0 ;;
    --skip-install) INSTALL=0 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; usage >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

BACKEND_URL="http://localhost:5000"
# Local dev default only, same as docker-compose.yml. Never use it outside this machine.
BUILDERS_SECRET="${BUILDERS_INTERNAL_SECRET:-builders-dev-only-secret}"

log() { printf '\033[1;34m[run]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[run]\033[0m %s\n' "$*" >&2; exit 1; }

# --- Tooling -------------------------------------------------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi
command -v dotnet >/dev/null 2>&1 || die "dotnet not found (need .NET 10 SDK)."
command -v pnpm >/dev/null 2>&1 || die "pnpm not found (corepack enable, or npm i -g pnpm@10)."
command -v docker >/dev/null 2>&1 || die "docker not found (needed for Redis and Postgres)."

# --- AWS (Bedrock, KMS) --------------------------------------------------------------------------------------------
# The backend uses the default credential chain. Without a valid SSO session it still starts, but chat answers fail.
export AWS_PROFILE="${AWS_PROFILE:-hackathon}"
export AWS_REGION="${AWS_REGION:-eu-central-1}"
if command -v aws >/dev/null 2>&1 && aws sts get-caller-identity --profile "$AWS_PROFILE" >/dev/null 2>&1; then
  log "AWS profile '$AWS_PROFILE' is signed in."
else
  log "WARNING: AWS profile '$AWS_PROFILE' has no valid session; chat calls to Bedrock will fail."
  log "         Run: aws sso login --profile $AWS_PROFILE"
fi

# --- Dependencies --------------------------------------------------------------------------------------------------
log "Starting Redis and Postgres (docker compose)..."
docker compose up -d --wait redis postgres

if [ "$INSTALL" = 1 ]; then
  log "Installing Node packages..."
  pnpm install --frozen-lockfile
fi

log "Building the backend..."
dotnet build backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj -nologo -v quiet

# --- Processes -----------------------------------------------------------------------------------------------------
PIDS=()
cleanup() {
  trap - INT TERM EXIT
  log "Stopping..."
  for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap cleanup INT TERM EXIT

if [ "$WITH_BUILDERS" = 1 ]; then
  log "Starting builders sidecar on :3100..."
  BUILDERS_INTERNAL_SECRET="$BUILDERS_SECRET" \
  ALGOD_URL_TESTNET="${ALGOD_URL_TESTNET:-https://testnet-api.4160.nodely.dev}" \
  ALGOD_URL_MAINNET="${ALGOD_URL_MAINNET:-https://mainnet-api.4160.nodely.dev}" \
    pnpm --filter builders dev &
  PIDS+=($!)
  export Builders__BaseAddress="http://127.0.0.1:3100"
  export Builders__InternalSecret="$BUILDERS_SECRET"
fi

if [ "$WITH_MOCK_OIDC" = 1 ]; then
  log "Starting mock Biatec OIDC on :8089..."
  pnpm --filter mock-oidc start &
  PIDS+=($!)
fi

log "Starting backend on $BACKEND_URL (Development: migrates the database and seeds tenants)..."
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$BACKEND_URL" \
  dotnet run --project backend/Biatec.Chat.Api --no-build --no-launch-profile &
PIDS+=($!)

if [ "$WITH_WIDGET" = 1 ]; then
  log "Starting widget playground on http://localhost:5173 ..."
  # Port is fixed: the seeded localhost tenant only allows the origins :5173 and :3000.
  pnpm -C packages/chat-widget dev --port 5173 --strictPort &
  PIDS+=($!)
fi

for _ in $(seq 1 60); do
  curl -fs "$BACKEND_URL/health/live" >/dev/null 2>&1 && break
  sleep 1
done
if curl -fs "$BACKEND_URL/health" >/dev/null 2>&1; then
  log "Backend healthy: $BACKEND_URL/health"
else
  log "WARNING: backend not healthy yet; check the output above."
fi
[ "$WITH_WIDGET" = 1 ] && log "Open http://localhost:5173 (chat launcher bottom right). Ctrl+C stops everything."

wait -n "${PIDS[@]}"
log "A process exited; shutting the rest down."
