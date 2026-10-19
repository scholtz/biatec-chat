# Build from the repository root:
#   docker build -f packages/consent-protocol/mock-oidc/Dockerfile -t biatec-mock-oidc .
FROM node:24-alpine
WORKDIR /app/packages/consent-protocol
COPY packages/consent-protocol/schemas ./schemas
COPY packages/consent-protocol/vectors ./vectors
COPY packages/consent-protocol/scripts/lib.mjs ./scripts/lib.mjs
COPY packages/consent-protocol/mock-oidc/package.json ./mock-oidc/package.json
WORKDIR /app/packages/consent-protocol/mock-oidc
RUN npm install --omit=dev --ignore-scripts --no-package-lock
COPY packages/consent-protocol/mock-oidc/src ./src
ENV PORT=8089 HOST=0.0.0.0
EXPOSE 8089
USER node
CMD ["node", "src/server.ts"]
