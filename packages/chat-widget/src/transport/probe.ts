import { CLIENT_KEY_HEADER, PATHS, type TransportConfig } from "./types";

export type ProbeResult = "success" | "failed-registered" | "failed-unregistered";

async function probeOnce(cfg: TransportConfig, f: typeof fetch): Promise<boolean> {
  const res = await f(cfg.backendUrl + PATHS.probe, { credentials: "include", headers: { [CLIENT_KEY_HEADER]: cfg.clientKey } });
  if (!res.ok) throw new Error(`probe ${res.status}`);
  return ((await res.json()) as { cookieReturned?: boolean }).cookieReturned === true;
}

/**
 * Two credentialed requests: the first sets the cookie, the second says whether it came back. A network, CORS or
 * HTTP failure on either counts as "cookie not returned".
 */
export async function probeCookie(cfg: TransportConfig): Promise<boolean> {
  const f = cfg.fetch ?? ((...a: Parameters<typeof fetch>) => fetch(...a));
  try {
    await probeOnce(cfg, f);
    return await probeOnce(cfg, f);
  } catch {
    return false;
  }
}
