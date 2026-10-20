# Demo site (docs/demo-site.md). Build context is the repository root: docker build -f apps/demo/Dockerfile .
# BACKEND_URL is baked into the bundle and into the CSP (connect-src), so the image is built per environment; the
# stage pipeline builds it with the default. CLIENT_KEY is a label registered on the tenant, not a secret.
FROM node:22-alpine AS build
ARG BACKEND_URL=https://stage.chat.biatec.io
ARG CLIENT_KEY=biatec-demo
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY packages/chat-protocol/package.json packages/chat-protocol/
COPY packages/chat-widget/package.json packages/chat-widget/
COPY apps/demo/package.json apps/demo/
RUN pnpm install --frozen-lockfile --filter demo...
COPY packages/chat-protocol/src packages/chat-protocol/src
COPY packages/chat-protocol/tsconfig.json packages/chat-protocol/
COPY packages/chat-widget/src packages/chat-widget/src
COPY packages/chat-widget/scripts packages/chat-widget/scripts
COPY packages/chat-widget/playground packages/chat-widget/playground
COPY packages/chat-widget/vite.config.ts packages/chat-widget/tsconfig.json packages/chat-widget/
COPY apps/demo/src apps/demo/src
COPY apps/demo/public apps/demo/public
COPY apps/demo/index.html apps/demo/dex.html apps/demo/vite.config.ts apps/demo/tsconfig.json apps/demo/nginx.conf apps/demo/
RUN pnpm --filter @biatec/chat-protocol build \
 && pnpm --filter @biatec/chat-widget build \
 && VITE_BIATEC_CHAT_URL="$BACKEND_URL" VITE_BIATEC_CHAT_CLIENT_KEY="$CLIENT_KEY" pnpm --filter demo build
# The bundle must hold the backend URL it was built for and nothing that looks like a credential.
RUN grep -rqF "$BACKEND_URL" apps/demo/dist \
 && ! grep -rEil "client_secret|refresh_token|access_token|BEGIN [A-Z ]*PRIVATE KEY|AKIA[0-9A-Z]{16}" apps/demo/dist
RUN origin="$(printf '%s' "$BACKEND_URL" | sed -E 's#^(https?://[^/]+).*#\1#')" \
 && sed "s#__BACKEND_ORIGIN__#$origin#" apps/demo/nginx.conf > /nginx.conf \
 && ! grep -q __BACKEND_ORIGIN__ /nginx.conf

FROM nginxinc/nginx-unprivileged:1.27-alpine
COPY --from=build /nginx.conf /etc/nginx/nginx.conf
COPY --from=build /repo/apps/demo/dist /usr/share/nginx/html
USER 101
EXPOSE 8080
# nginx directly: the image's entrypoint scripts would try to rewrite files on the read-only root filesystem.
ENTRYPOINT ["nginx", "-g", "daemon off;"]
