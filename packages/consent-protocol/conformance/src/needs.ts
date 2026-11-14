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
/** True when the suite may use the mock's seed data and helpers: the in-process mock, or a mock that was told so. */
export const seeded = (env: Env = process.env) => !serverUrl(env) || env.CONFORMANCE_MOCK_HELPERS === "1";

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
    case "mockHelpers": return false;
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

/** Every secret value the run was given, for redaction. */
export function secretValues(env: Env = process.env): string[] {
  const names = ["CONFORMANCE_CLIENT_SECRET", "CONFORMANCE_USER_TOKEN", "CONFORMANCE_STEPUP_BEARER", "CONFORMANCE_DEVICE_TOKEN", "CONFORMANCE_DEVICE_PRIVATE_JWK", ...Object.values(bearerVars)];
  const out = new Set<string>();
  for (const n of names) {
    const v = env[n];
    if (!v) continue;
    out.add(v);
    try { // the private JWK's members individually
      const j = JSON.parse(v);
      if (j && typeof j === "object") for (const m of Object.values(j)) if (typeof m === "string" && m.length >= 8) out.add(m);
    } catch { /* not JSON */ }
  }
  return [...out].sort((a, b) => b.length - a.length);
}

/** Replaces the supplied secrets and anything shaped like a token, a code or an auth request id. */
export function redact(text: string, env: Env = process.env): string {
  let t = text;
  for (const s of secretValues(env)) t = t.split(s).join("[redacted]");
  return t
    .replace(/(Bearer|Basic|Device)\s+[A-Za-z0-9._~+/=-]{6,}/gi, "$1 [redacted]")
    .replace(/eyJ[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]+){1,2}/g, "[redacted-jwt]")
    .replace(/(\b(?:access_token|refresh_token|id_token|binding_code|code|auth_req_id|client_secret|signature|token|private_key|d)\b"?\s*[:=]\s*)("[^"]*"|[A-Za-z0-9._~+/=-]+)/gi, (_m, k) => `${k}"[redacted]"`)
    .replace(/\b(ciba_[A-Za-z0-9_-]+)\b/g, "[redacted-auth-req]");
}
