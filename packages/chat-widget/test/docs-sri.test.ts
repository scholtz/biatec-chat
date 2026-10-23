import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// BCHAT-027: the documented CDN embedding carries Subresource Integrity and pins an exact version. It is not tied to package.json's
// version: a release PR cannot know the hash of a file that exists only after the publish, so the snippets move to the new version and
// hash in a follow-up that copies the value from the release notes.
const files = ["README.md", "../../docs/integration-widget.md"].map((f) => [f, readFileSync(resolve(__dirname, "..", f), "utf8").replace(/\r\n/g, "\n")] as const);

describe("CDN snippets", () => {
  it.each(files)("%s: every script tag loading the standalone file has integrity, crossorigin and the current version", (_name, text) => {
    const tags = [...text.matchAll(/<script\b[^>]*biatec-chat\.standalone\.iife\.js[^>]*>/g)].map((m) => m[0]);
    expect(tags.length).toBeGreaterThan(0);
    for (const tag of tags) {
      expect(tag).toMatch(/integrity="sha384-[A-Za-z0-9+/]{64}"/);
      expect(tag).toMatch(/crossorigin="anonymous"/);
      expect(tag).toMatch(/@biatec\/chat-widget@\d+\.\d+\.\d+\//);
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
    // The file is extracted before it is hashed (no pipe that could hide a tar failure), checked non-empty, hashed with SHA-384,
    // and the value reaches the release notes of a new release and, if the release exists without it, an edit of the old one.
    expect(wf).toMatch(/tar -xzOf artifacts\/\*\.tgz package\/dist\/standalone\/biatec-chat\.standalone\.iife\.js > standalone\.iife\.js/);
    expect(wf).toMatch(/\[ -s standalone\.iife\.js \]/);
    expect(wf).toMatch(/openssl dgst -sha384 -binary standalone\.iife\.js \| openssl base64 -A/);
    expect(wf.match(/printf '\\nSubresource Integrity of/g)?.length).toBe(2);
    expect(wf).toContain("gh release edit");
  });
});
