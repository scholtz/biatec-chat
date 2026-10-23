# Build context is the repository root: docker compose build builders
FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS build
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY builders/sidecar/package.json builders/sidecar/
RUN pnpm install --frozen-lockfile --filter builders...
COPY builders/sidecar builders/sidecar
RUN pnpm --filter builders build
FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914
ENV NODE_ENV=production PORT=3100
WORKDIR /repo
# pnpm's node_modules are symlinks into the root .pnpm store, so both keep their relative layout.
COPY --from=build /repo/node_modules ./node_modules
COPY --from=build /repo/builders/sidecar/node_modules ./builders/sidecar/node_modules
COPY --from=build /repo/builders/sidecar/dist ./builders/sidecar/dist
COPY --from=build /repo/builders/sidecar/package.json ./builders/sidecar/
WORKDIR /repo/builders/sidecar
USER node
EXPOSE 3100
HEALTHCHECK --interval=5s --timeout=3s --retries=10 CMD wget -qO- http://127.0.0.1:3100/health || exit 1
CMD ["node", "dist/main.js"]
