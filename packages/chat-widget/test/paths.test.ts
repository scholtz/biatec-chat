import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { PATHS } from "../src/transport/types";

// The backend's OpenAPI document (regenerated into chat-protocol) is the source of truth for routes. Every path the
// widget calls must be one the backend serves; a mismatch here broke chat on every cookie-transport host once.
const openapi = JSON.parse(
  readFileSync(resolve(process.cwd(), "../chat-protocol/openapi.json"), "utf8"),
) as { paths: Record<string, unknown> };

// Served by the backend but not part of the OpenAPI document (a file download, not a JSON contract).
const NOT_IN_OPENAPI = new Set<string>([PATHS.evidence]);

describe("widget backend paths", () => {
  it.each(Object.entries(PATHS))("%s (%s) is a backend route", (_name, path) => {
    if (NOT_IN_OPENAPI.has(path)) return;
    expect(Object.keys(openapi.paths)).toContain(path);
  });
});
