# Build from the repository root:
#   docker build -f packages/consent-protocol/mock-oidc/Dockerfile -t biatec-mock-oidc .
# Dependencies come from the workspace lock (pnpm-lock.yaml, --frozen-lockfile), like every other image, so a build never resolves new versions.
FROM node:24-alpine AS build
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml .npmrc ./
COPY packages/consent-protocol/mock-oidc/package.json packages/consent-protocol/mock-oidc/
RUN pnpm install --frozen-lockfile --prod --filter mock-oidc...

FROM node:24-alpine
WORKDIR /repo
# pnpm's node_modules are symlinks into the root .pnpm store, so both keep their relative layout.
COPY --from=build /repo/node_modules ./node_modules
COPY --from=build /repo/packages/consent-protocol/mock-oidc/node_modules ./packages/consent-protocol/mock-oidc/node_modules
COPY packages/consent-protocol/schemas ./packages/consent-protocol/schemas
COPY packages/consent-protocol/vectors ./packages/consent-protocol/vectors
COPY packages/consent-protocol/scripts/lib.mjs ./packages/consent-protocol/scripts/lib.mjs
COPY packages/consent-protocol/mock-oidc/package.json ./packages/consent-protocol/mock-oidc/package.json
COPY packages/consent-protocol/mock-oidc/src ./packages/consent-protocol/mock-oidc/src
WORKDIR /repo/packages/consent-protocol/mock-oidc
# Loopback by default; set HOST=0.0.0.0 (and read the README) only on a private network. Test helpers stay off unless MOCK_TEST_HELPERS=1.
ENV PORT=8089 HOST=127.0.0.1
EXPOSE 8089
USER node
CMD ["node", "src/server.ts"]
