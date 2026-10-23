import { generateKeyPairSync, sign } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { bundleSigningInput, entryHash, payloadHash, signingInput, verifyBundle, type EvidenceBundle, type Jwks } from "../src/index.js";

// bundle.json and jwks.json are emitted by the real backend (EvidenceExportTests with EVIDENCE_FIXTURE_DIR set):
// 3 entries, signed by two keys (one rotated out), then again with every payload deleted.
const dir = join(dirname(fileURLToPath(import.meta.url)), "fixtures");
const read = <T>(f: string): T => JSON.parse(readFileSync(join(dir, f), "utf8")) as T;
const jwks = read<Jwks>("jwks.json");
const fresh = () => read<EvidenceBundle>("bundle.json");

describe("backend-exported bundle", () => {
  it("verifies against the published JWKS", () => {
    const r = verifyBundle(fresh(), { jwks });
    expect(r.errors).toEqual([]);
    expect(r.valid).toBe(true);
    expect(r.keysAuthenticated).toBe(true);
    expect(r.entries).toBe(3);
    expect(r.payloadsVerified).toBe(3);
  });

  it("still verifies when every payload was deleted", () => {
    const r = verifyBundle(read<EvidenceBundle>("bundle-deleted.json"), { jwks });
    expect(r.errors).toEqual([]);
    expect(r.valid).toBe(true);
    expect(r.payloadsDeleted).toBe(3);
    expect(r.payloadsVerified).toBe(0);
  });

  it("refuses to run without a JWKS unless embedded keys are explicitly allowed", () => {
    expect(verifyBundle(fresh()).valid).toBe(false);
    const r = verifyBundle(fresh(), { allowEmbeddedKeys: true });
    expect(r.valid).toBe(true);
    expect(r.keysAuthenticated).toBe(false);
    expect(r.warnings.length).toBeGreaterThan(0);
  });
});

describe("what a valid result does not say (BCHAT-022)", () => {
  it("counts anchored and unanchored entries and always warns that anchors are not resolved", () => {
    const r = verifyBundle(fresh(), { jwks });
    expect(r.valid).toBe(true);
    expect(r.anchoredEntries + r.unanchoredEntries).toBe(r.entries);
    expect(r.warnings.some((w) => /anchor/i.test(w))).toBe(true);
  });

  /** A self-signed bundle whose entries carry the given anchorRefs (verified with allowEmbeddedKeys: only the anchoring wording matters here). */
  function selfSigned(anchors: (string | null)[]): EvidenceBundle {
    const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
    const jwk = publicKey.export({ format: "jwk" });
    const kid = "ev-anchor-test";
    const es = (text: string) => sign("sha256", Buffer.from(text), { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64url");
    const src = fresh();
    let prev = "0".repeat(64);
    const entries = anchors.map((anchorRef, i) => {
      const e = src.entries[i % src.entries.length];
      const seq = i + 1;
      const ph = payloadHash(e.salt!, e.payload);
      const eh = entryHash({ conversationId: src.conversationId, seq, prevHash: prev, kind: e.kind, payloadHash: ph, anchorRef });
      const signedAt = "2026-10-20T10:00:00.000Z";
      const out = { ...e, seq, anchorRef, payloadHash: ph, prevHash: prev, entryHash: eh, kid, signedAt, signature: es(signingInput(eh, kid, signedAt)) };
      prev = eh;
      return out;
    });
    const b: EvidenceBundle = { ...src, kid, entries, keys: [{ kty: "EC", crv: "P-256", alg: "ES256", use: "sig", kid, x: jwk.x!, y: jwk.y!, created_at: "2026-10-01T00:00:00.000Z" }], signature: "" };
    b.signature = es(bundleSigningInput(b));
    return b;
  }

  it("says the log is not yet anchored when no entry carries an anchorRef", () => {
    const r = verifyBundle(selfSigned([null, null, null]), { allowEmbeddedKeys: true });
    expect(r.valid).toBe(true);
    expect([r.anchoredEntries, r.unanchoredEntries]).toEqual([0, 3]);
    expect(r.warnings.join(" ")).toMatch(/No entry is anchored yet/);
  });

  it("counts anchored entries but still says they were not resolved", () => {
    const r = verifyBundle(selfSigned([null, "A".repeat(52), "B".repeat(52)]), { allowEmbeddedKeys: true });
    expect(r.valid).toBe(true);
    expect([r.anchoredEntries, r.unanchoredEntries]).toEqual([2, 1]);
    expect(r.warnings.join(" ")).toMatch(/2 of 3 entries carry an anchorRef, but this verifier does not resolve anchors/);
  });

  it("an invalid bundle gets no anchoring caveat (it already fails)", () => {
    const b = fresh();
    (b.entries[0].payload as Record<string, unknown>).text = "edited";
    const r = verifyBundle(b, { jwks });
    expect(r.valid).toBe(false);
    expect(r.warnings.some((w) => /anchor/i.test(w))).toBe(false);
  });
});

describe("tampered bundles fail", () => {
  const fails = (b: unknown, pattern: RegExp) => {
    const r = verifyBundle(b, { jwks });
    expect(r.valid).toBe(false);
    expect(r.errors.join("\n")).toMatch(pattern);
  };

  it("edited payload", () => {
    const b = fresh();
    (b.entries[1].payload as Record<string, unknown>).text = "edited";
    fails(b, /Entry 2: payloadHash/);
  });

  it("edited kind or anchorRef", () => {
    const b = fresh();
    b.entries[0].kind = "other";
    fails(b, /Entry 1: entryHash/);
    const c = fresh();
    c.entries[0].anchorRef = "00000000-0000-0000-0000-000000000001";
    fails(c, /Entry 1: entryHash/);
  });

  it("removed entry", () => {
    const b = fresh();
    b.entries.splice(1, 1);
    fails(b, /expected seq 2/);
  });

  it("reordered entries", () => {
    const b = fresh();
    [b.entries[0], b.entries[1]] = [b.entries[1], b.entries[0]];
    fails(b, /expected seq 1/);
  });

  it("entry moved to another conversation", () => {
    const b = fresh();
    b.conversationId = "11111111-2222-3333-4444-555555555555";
    fails(b, /entryHash/);
  });

  it("payload and salt removed by an attacker is only accepted as a deletion, and edited hash breaks the chain", () => {
    const b = fresh();
    b.entries[1].payloadHash = "a".repeat(64);
    b.entries[1].payload = null;
    b.entries[1].salt = null;
    fails(b, /Entry 2: entryHash/);
  });

  it("payload without salt", () => {
    const b = fresh();
    b.entries[0].salt = null;
    fails(b, /present or deleted together/);
  });

  it("altered signature, kid or signedAt", () => {
    const b = fresh();
    const s = b.entries[2].signature;
    b.entries[2].signature = (s[0] === "A" ? "B" : "A") + s.slice(1);
    fails(b, /Entry 3: signature/);
    const c = fresh();
    c.entries[0].signedAt = "2030-01-01T00:00:00.000Z";
    fails(c, /Entry 1: (signature|signedAt)/);
    const d = fresh();
    d.entries[0].kid = d.entries[2].kid === d.entries[0].kid ? "ev-unknown" : d.entries[2].kid;
    fails(d, /Entry 1: (signature|signedAt|unknown)/);
  });

  it("altered bundle envelope (exportedAt)", () => {
    const b = fresh();
    b.exportedAt = "2020-01-01T00:00:00.000Z";
    fails(b, /Bundle signature/);
  });

  it("wrong format or version", () => {
    fails({ ...fresh(), format: "x" }, /format/);
    fails({ ...fresh(), version: 2 }, /version/);
    fails({ ...fresh(), entries: [] }, /no entries/);
    fails("nope", /not a JSON object/);
  });
});

describe("keys that are not in the JWKS", () => {
  /** An attacker re-signs a complete, self-consistent bundle with their own key and embeds it. */
  function forge(): EvidenceBundle {
    const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
    const jwk = publicKey.export({ format: "jwk" });
    const kid = "ev-forged";
    const es = (text: string) => sign("sha256", Buffer.from(text), { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64url");
    const src = fresh();
    let prev = "0".repeat(64);
    const entries = src.entries.map((e) => {
      const salt = e.salt!;
      const ph = payloadHash(salt, e.payload);
      const eh = entryHash({ conversationId: src.conversationId, seq: e.seq, prevHash: prev, kind: e.kind, payloadHash: ph, anchorRef: e.anchorRef });
      const signedAt = "2026-10-20T10:00:00.000Z";
      const out = { ...e, payloadHash: ph, prevHash: prev, entryHash: eh, kid, signedAt, signature: es(signingInput(eh, kid, signedAt)) };
      prev = eh;
      return out;
    });
    const b: EvidenceBundle = { ...src, kid, entries, keys: [{ kty: "EC", crv: "P-256", alg: "ES256", use: "sig", kid, x: jwk.x!, y: jwk.y!, created_at: "2026-10-01T00:00:00.000Z" }], signature: "" };
    b.signature = es(bundleSigningInput(b));
    return b;
  }

  it("is self-consistent but fails against the JWKS", () => {
    const forged = forge();
    const self = verifyBundle(forged, { allowEmbeddedKeys: true });
    expect(self.valid).toBe(true);
    expect(self.keysAuthenticated).toBe(false);

    const r = verifyBundle(forged, { jwks });
    expect(r.valid).toBe(false);
    expect(r.errors.join("\n")).toMatch(/not in the published JWKS/);
  });

  it("fails when an embedded key is swapped for a different key with a real kid", () => {
    const forged = forge();
    const real = fresh();
    forged.keys[0].kid = real.keys[0].kid;
    expect(verifyBundle(forged, { jwks }).valid).toBe(false);
  });

  it("fails when the JWKS does not contain the signing key at all", () => {
    const r = verifyBundle(fresh(), { jwks: { keys: [] } });
    expect(r.valid).toBe(false);
    expect(r.errors.join("\n")).toMatch(/not in the published JWKS|not trusted/);
  });
});

describe("CLI", () => {
  const cli = join(dirname(fileURLToPath(import.meta.url)), "..", "cli.mjs");
  const run = (...args: string[]) => spawnSync(process.execPath, [cli, ...args], { encoding: "utf8" });

  it("exits 0 for a good bundle and 1 for a tampered one", () => {
    const ok = run("verify", join(dir, "bundle.json"), "--jwks", join(dir, "jwks.json"));
    expect(ok.status).toBe(0);
    expect(ok.stdout).toMatch(/^OK: 3 entries/m);
    expect(ok.stdout).toMatch(/anchors were NOT checked/);
    expect(ok.stdout).toMatch(/WARNING: .*anchor/i);

    const b = fresh();
    (b.entries[0].payload as Record<string, unknown>).text = "edited";
    const tmp = join(mkdtempSync(join(tmpdir(), "ev-")), "tampered.json");
    writeFileSync(tmp, JSON.stringify(b));
    const bad = run("verify", tmp, "--jwks", join(dir, "jwks.json"));
    expect(bad.status).toBe(1);
    expect(bad.stdout).toMatch(/INVALID/);
  });

  it("verifies a bundle with deleted payloads", () => {
    expect(run("verify", join(dir, "bundle-deleted.json"), "--jwks", join(dir, "jwks.json")).status).toBe(0);
  });

  it("exits 2 without a JWKS, with a missing file or with unknown options", () => {
    expect(run("verify", join(dir, "bundle.json")).status).toBe(2);
    expect(run("verify", join(dir, "nope.json"), "--jwks", join(dir, "jwks.json")).status).toBe(2);
    expect(run("verify", join(dir, "bundle.json"), "--bogus").status).toBe(2);
  });

  it("warns when only embedded keys are used", () => {
    const r = run("verify", join(dir, "bundle.json"), "--insecure-embedded-keys");
    expect(r.status).toBe(0);
    expect(r.stdout).toMatch(/WARNING/);
  });
});
