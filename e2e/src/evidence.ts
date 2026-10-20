// A reference recorder for the evidence log, used by the e2e run in place of the .NET backend's PostgreSQL log (which needs
// Bedrock and a database to run a whole conversation). It follows the spec's Evidence Record exactly and shares its hashing
// and canonicalisation code with the offline verifier, so the bundle it emits must pass the same checks as a real export.
// The kinds are the ones the backend writes (backend/Biatec.Chat.Core/Evidence/Recording/EvidenceRecording.cs).
import { createPrivateKey, generateKeyPairSync, randomBytes, randomUUID, sign } from "node:crypto";
import { bundleSigningInput, entryHash, payloadHash, signingInput } from "../../packages/evidence-verifier/src/verify.ts";
import type { BundleEntry, EvidenceBundle, Jwk, Jwks } from "../../packages/evidence-verifier/src/verify.ts";

const GENESIS = "0".repeat(64);

export class EvidenceKey {
  readonly kid: string;
  readonly jwk: Jwk;
  private readonly priv;

  constructor(kid = "evidence-e2e-1", createdAt = new Date(Date.now() - 3_600_000)) {
    const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
    this.priv = createPrivateKey(privateKey.export({ format: "pem", type: "pkcs8" }));
    const pub = publicKey.export({ format: "jwk" }) as { x: string; y: string };
    this.kid = kid;
    this.jwk = { kty: "EC", crv: "P-256", alg: "ES256", use: "sig", kid, x: pub.x, y: pub.y, created_at: createdAt.toISOString() };
  }

  get jwks(): Jwks { return { keys: [this.jwk] }; }

  signEs256(text: string): string {
    return sign("sha256", Buffer.from(text, "utf8"), { key: this.priv, dsaEncoding: "ieee-p1363" }).toString("base64url");
  }
}

export class EvidenceRecorder {
  readonly conversationId = randomUUID();
  readonly entries: BundleEntry[] = [];

  constructor(private readonly key: EvidenceKey) {}

  append(kind: string, payload: Record<string, unknown>, anchorRef: string | null = null): BundleEntry {
    const salt = randomBytes(32).toString("hex");
    const prevHash = this.entries.at(-1)?.entryHash ?? GENESIS;
    const seq = this.entries.length + 1;
    const ph = payloadHash(salt, payload);
    const eh = entryHash({ conversationId: this.conversationId, seq, prevHash, kind, payloadHash: ph, anchorRef });
    const signedAt = new Date().toISOString();
    const entry: BundleEntry = {
      seq, kind, anchorRef, payload, salt, payloadHash: ph, prevHash, entryHash: eh,
      kid: this.key.kid, signedAt, signature: this.key.signEs256(signingInput(eh, this.key.kid, signedAt)),
    };
    this.entries.push(entry);
    return entry;
  }

  /** GET /session/evidence: the whole chain, the keys, and a signature over the rest. */
  export(): EvidenceBundle {
    const bundle: EvidenceBundle = {
      format: "biatec-evidence-bundle",
      version: 1,
      conversationId: this.conversationId,
      exportedAt: new Date().toISOString(),
      kid: this.key.kid,
      keys: [this.key.jwk],
      entries: structuredClone(this.entries),
      signature: "",
    };
    bundle.signature = this.key.signEs256(bundleSigningInput(bundle));
    return bundle;
  }
}
