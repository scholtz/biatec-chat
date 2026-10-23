import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// BCHAT-027: the documented CDN embedding carries Subresource Integrity and pins the exact version of this package.
const version = (JSON.parse(readFileSync(resolve(__dirname, "../package.json"), "utf8")) as { version: string }).version;
const files = ["README.md", "../../docs/integration-widget.md"].map((f) => [f, readFileSync(resolve(__dirname, "..", f), "utf8").replace(/\r\n/g, "\n")] as const);

describe("CDN snippets", () => {
  it.each(files)("%s: every script tag loading the standalone file has integrity, crossorigin and the current version", (_name, text) => {
    const tags = [...text.matchAll(/<script\b[^>]*biatec-chat\.standalone\.iife\.js[^>]*>/g)].map((m) => m[0]);
    expect(tags.length).toBeGreaterThan(0);
    for (const tag of tags) {
      expect(tag).toMatch(/integrity="sha384-[A-Za-z0-9+/]{64}"/);
      expect(tag).toMatch(/crossorigin="anonymous"/);
      expect(tag).toContain(`@biatec/chat-widget@${version}/`);
    }
  });

  it("both documents show the same hash", () => {
    const hashes = files.map(([, text]) => text.match(/integrity="(sha384-[^"]+)"/)?.[1]);
    expect(hashes[0]).toBeDefined();
    expect(hashes[0]).toBe(hashes[1]);
  });
});

describe("release workflow", () => {
  it("writes the SRI hash of the published standalone file to the release notes", () => {
    const wf = readFileSync(resolve(__dirname, "../../../.github/workflows/publish-widget.yml"), "utf8");
    expect(wf).toContain("openssl dgst -sha384 -binary");
    expect(wf).toContain("package/dist/standalone/biatec-chat.standalone.iife.js");
    expect(wf).toMatch(/Subresource Integrity/);
  });
});
