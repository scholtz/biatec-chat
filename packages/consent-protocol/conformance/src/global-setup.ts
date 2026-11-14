// Starts the T-047 mock OIDC consent server unless CONFORMANCE_OIDC_URL (or the older CONFORMANCE_BASE_URL) points the suite at another server.
import type { AddressInfo } from "node:net";
import type { TestProject } from "vitest/node";
import { createMockOidc } from "../../mock-oidc/src/app.ts";
import { serverUrl } from "./needs.ts";

declare module "vitest" {
  export interface ProvidedContext {
    baseUrl: string;
    target: "mock" | "external";
  }
}

export default async function setup(project: TestProject) {
  const external = serverUrl();
  if (external) {
    project.provide("baseUrl", external);
    project.provide("target", "external");
    return;
  }
  const mock = createMockOidc({ testHelpers: true });
  await new Promise<void>((resolve) => mock.server.listen(0, "127.0.0.1", resolve));
  project.provide("baseUrl", `http://127.0.0.1:${(mock.server.address() as AddressInfo).port}`);
  project.provide("target", "mock");
  return () => new Promise<void>((resolve) => mock.server.close(() => resolve()));
}
