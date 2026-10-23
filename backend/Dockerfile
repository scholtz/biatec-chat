# Build context is the repository root: docker build -f backend/Dockerfile .
# The administrator dashboard (docs/admin-dashboard.md): a Vue app the backend serves at /admin/. It holds no secret and no URL.
FROM node:25-alpine@sha256:bdf2cca6fe3dabd014ea60163eca3f0f7015fbd5c7ee1b0e9ccb4ced6eb02ef4 AS admin-ui
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/admin/package.json apps/admin/
RUN pnpm install --frozen-lockfile --filter @biatec/admin...
COPY apps/admin apps/admin
RUN pnpm --filter @biatec/admin build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src
COPY backend/Biatec.Chat.Core/Biatec.Chat.Core.csproj backend/Biatec.Chat.Core/
COPY backend/Biatec.Chat.Tools/Biatec.Chat.Tools.csproj backend/Biatec.Chat.Tools/
COPY backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj backend/Biatec.Chat.Api/
RUN dotnet restore backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj
COPY backend/Biatec.Chat.Core backend/Biatec.Chat.Core
COPY backend/Biatec.Chat.Tools backend/Biatec.Chat.Tools
COPY backend/Biatec.Chat.Api backend/Biatec.Chat.Api
# Biatec.Chat.Api.csproj links docs/kb/**/*.md into the output as kb/ (the local KB and the kb.search fallback).
COPY docs/kb docs/kb
RUN dotnet publish backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

# AWS IAM Roles Anywhere credential helper (Bedrock access without access keys or a cluster OIDC issuer AWS can reach).
# The AWS SDK runs it through credential_process (k8s/base/backend.yaml, docs/aws-setup.md). Pinned and checksummed.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca AS signing-helper
ARG TARGETARCH
ARG SIGNING_HELPER_VERSION=1.8.0
ARG SIGNING_HELPER_SHA256_AMD64=4ec587c4b2a7a4b2980f0fb084c3fabd99a862ce1091341ba514ae3c0923b905
ARG SIGNING_HELPER_SHA256_ARM64=eb1c70b8ef8b02326cd0f177572285d605361920ff22ae0876bb4172e936f968
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
      amd64) arch=X86_64; sha="$SIGNING_HELPER_SHA256_AMD64" ;; \
      arm64) arch=Aarch64; sha="$SIGNING_HELPER_SHA256_ARM64" ;; \
      *) echo "unsupported arch ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*; \
    curl -fsSL -o /aws_signing_helper "https://rolesanywhere.amazonaws.com/releases/${SIGNING_HELPER_VERSION}/${arch}/Linux/Amzn2023/aws_signing_helper"; \
    echo "${sha}  /aws_signing_helper" | sha256sum -c -; \
    chmod 0755 /aws_signing_helper

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
ENV ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=signing-helper /aws_signing_helper /usr/local/bin/aws_signing_helper
COPY --from=build /out .
COPY --from=admin-ui /repo/apps/admin/dist ./admin-ui
# The aspnet image ships a non-root "app" user (UID 1654); numeric so the kubelet can verify runAsNonRoot.
USER 1654
EXPOSE 8080
CMD ["dotnet", "Biatec.Chat.Api.dll"]
