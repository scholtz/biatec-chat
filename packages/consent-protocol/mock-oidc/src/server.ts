import { createMockOidc } from "./app.ts";

const port = Number(process.env.PORT ?? 8089);
const host = process.env.HOST ?? "127.0.0.1";
const { server, issuer } = createMockOidc();
server.listen(port, host, () => console.log(`mock-oidc (${issuer}) listening on http://${host}:${port}`));
