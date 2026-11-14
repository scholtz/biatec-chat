// What a conformance case needs, and whether this run can offer it. No imports from the kit so the reporter, the global setup
// and the workers all agree. Against the in-process mock (no CONFORMANCE_OIDC_URL) everything is available. Against another
// server only what the person supplied in environment variables is, and `mockHelpers` only when the server is a mock that
// says so (CONFORMANCE_MOCK_HELPERS=1).

export type Need =
  | "mockHelpers" // GET /mock/* test helpers: the device attestation lookup, device tokens, seeded users
  | "clientSecret" // the verified client's secret (CONFORMANCE_CLIENT_SECRET)
  | "userToken" // access token of the user and client under test (CONFORMANCE_USER_TOKEN)
  | "deviceKey" // an enrolled device whose private key signs receipts (CONFORMANCE_DEVICE_PRIVATE_JWK + CONFORMANCE_DEVICE_TOKEN)
  | "bearer:noDevice"
  | "bearer:paused"
  | "bearer:capped"
  | "bearer:blocksUnproven"
  | "bearer:alwaysAsk"
  | "bearer:threshold5000";

type Env = Record<string, string | undefined>;

/** The server under test; empty when the in-process mock is started. The deprecated CONFORMANCE_BASE_URL still works. */
export const serverUrl = (env: Env = process.env) => (env.CONFORMANCE_OIDC_URL || env.CONFORMANCE_BASE_URL || "").replace(/\/+$/, "");
/** True only for the in-process mock: its seed users, tokens and the vector device key are defaults nowhere else. */
export const seeded = (env: Env = process.env) => !serverUrl(env);
/** The server offers the mock's /mock/* helpers: the in-process mock, or a standalone mock announced with CONFORMANCE_MOCK_HELPERS=1. */
export const offersHelpers = (env: Env = process.env) => seeded(env) || env.CONFORMANCE_MOCK_HELPERS === "1";

export const bearerVars: Record<string, string> = {
  noDevice: "CONFORMANCE_BEARER_NO_DEVICE",
  paused: "CONFORMANCE_BEARER_PAUSED",
  capped: "CONFORMANCE_BEARER_CAPPED",
  blocksUnproven: "CONFORMANCE_BEARER_UNPROVEN_BLOCKED",
  alwaysAsk: "CONFORMANCE_BEARER_ALWAYS_ASK",
  threshold5000: "CONFORMANCE_BEARER_THRESHOLD_5000",
};

const variables = (need: Need): string[] => {
  if (need === "mockHelpers") return ["CONFORMANCE_MOCK_HELPERS=1"];
  if (need === "clientSecret") return ["CONFORMANCE_CLIENT_SECRET"];
  if (need === "userToken") return ["CONFORMANCE_USER_TOKEN"];
  if (need === "deviceKey") return ["CONFORMANCE_DEVICE_PRIVATE_JWK", "CONFORMANCE_DEVICE_TOKEN"];
  return [bearerVars[need.slice("bearer:".length)]];
};

export function have(need: Need, env: Env = process.env): boolean {
  if (seeded(env)) return true;
  switch (need) {
    case "mockHelpers": return offersHelpers(env);
    case "clientSecret": return !!env.CONFORMANCE_CLIENT_SECRET;
    case "userToken": return !!(env.CONFORMANCE_USER_TOKEN || env.CONFORMANCE_STEPUP_BEARER);
    case "deviceKey": return !!(env.CONFORMANCE_DEVICE_PRIVATE_JWK && env.CONFORMANCE_DEVICE_TOKEN);
    default: return !!env[bearerVars[need.slice("bearer:".length)]];
  }
}

/** The reason a case cannot run in this run, or null when everything it needs is there. */
export function missing(needs: Need[], env: Env = process.env): string | null {
  const absent = needs.filter((n) => !have(n, env));
  if (!absent.length) return null;
  return "needs " + absent.map((n) => n + (n === "mockHelpers" ? " (the server is not a mock with test helpers)" : ` (set ${variables(n).join(" and ")})`)).join("; ");
}

const secretNames = (env: Env) => Object.keys(env).filter((n) => /^CONFORMANCE_(CLIENT_SECRET|USER_TOKEN|STEPUP_BEARER|DEVICE_TOKEN|DEVICE_PRIVATE_JWK|BEARER_.+)$/.test(n));

/** Every secret value the run was given (any CONFORMANCE_ secret variable, value of 4 characters or more), for redaction. */
export function secretValues(env: Env = process.env): string[] {
  const out = new Set<string>();
  for (const n of secretNames(env)) {
    const v = env[n];
    if (!v || v.length < 4) continue;
    out.add(v);
    try { // the private JWK's members individually
      const j = JSON.parse(v);
      if (j && typeof j === "object") for (const m of Object.values(j)) if (typeof m === "string" && m.length >= 4) out.add(m);
    } catch { /* not JSON */ }
  }
  return [...out].sort((a, b) => b.length - a.length);
}

/** A string that looks like a credential: a digit, a dot, or 16 characters or more (so "attestation" and "authentication" are not). */
const tokenLike = "(?=[A-Za-z0-9._~+/=-]*[0-9.])[A-Za-z0-9._~+/=-]{6,}|[A-Za-z0-9._~+/=-]{16,}";

/** Replaces every supplied secret (exact value) and anything shaped like a token, a code or an auth request id. */
export function redact(text: string, env: Env = process.env): string {
  let t = text;
  for (const s of secretValues(env)) t = t.split(s).join("[redacted]");
  return t
    .replace(new RegExp(String.raw`\b(Bearer|Basic|Device)\s+(?:${tokenLike})`, "g"), "$1 [redacted]")
    .replace(/eyJ[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]+){1,2}/g, "[redacted-jwt]")
    .replace(/(\b(?:access_token|refresh_token|id_token|binding_code|code|auth_req_id|client_secret|signature|token|private_key|d)\b"?\s*[:=]\s*)("[^"]*"|[A-Za-z0-9._~+/=-]+)/gi, (_m, k) => `${k}"[redacted]"`)
    .replace(/\b(ciba_[A-Za-z0-9_-]+)\b/g, "[redacted-auth-req]");
}
