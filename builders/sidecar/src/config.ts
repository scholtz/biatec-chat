export interface Config {
  port: number;
  /** Shared secret the .NET backend sends in X-Internal-Secret. Required; the service refuses to start without it. */
  internalSecret: string;
  /** algod base URL per network name (lowercase), from ALGOD_URL_<NETWORK> variables. */
  algod: Record<string, string>;
}

const PREFIX = "ALGOD_URL_";

export function loadConfig(env: Record<string, string | undefined> = process.env): Config {
  const internalSecret = env.BUILDERS_INTERNAL_SECRET ?? "";
  if (internalSecret.length < 16) {
    throw new Error("BUILDERS_INTERNAL_SECRET must be set to at least 16 characters.");
  }
  const algod: Record<string, string> = {};
  for (const [key, value] of Object.entries(env)) {
    if (key.startsWith(PREFIX) && value) algod[key.slice(PREFIX.length).toLowerCase().replaceAll("_", "-")] = value;
  }
  const port = Number(env.PORT ?? 3100);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("PORT is invalid.");
  return { port, internalSecret, algod };
}
