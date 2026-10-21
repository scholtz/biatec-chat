import { describe, expect, it } from "vitest";
import { AuthClient } from "../src/components/chat/auth";
import { resolveOptions } from "../src/options";
import { probeCookie } from "../src/transport/probe";
import { CookieTransport } from "../src/transport/cookie";
import { PATHS, evidenceDownloadUrl, type TransportConfig } from "../src/transport/types";

// The backend can be mounted under a path prefix of the page's own site (BiatecDEX: https://beta.dex.biatec.io/chat).
// Every widget URL is `backendUrl + path`, so a backendUrl with a path must keep that path in front of each route and
// the page must stay same-origin with it.
const MOUNTED = "https://beta.dex.biatec.io/chat";

function recorder(respond: (url: string) => Response = () => new Response("{}", { status: 200 })) {
  const urls: string[] = [];
  const credentials: (RequestCredentials | undefined)[] = [];
  const fetch: typeof globalThis.fetch = async (input, init) => {
    urls.push(String(input));
    credentials.push(init?.credentials);
    return respond(String(input));
  };
  return { urls, credentials, cfg: { backendUrl: resolveOptions({ backendUrl: MOUNTED + "///", clientKey: "biatec-dex" }).backendUrl, clientKey: "biatec-dex", fetch } satisfies TransportConfig };
}

describe("backendUrl with a path prefix", () => {
  it("is kept, with trailing slashes trimmed", () => {
    expect(resolveOptions({ backendUrl: MOUNTED, clientKey: "k" }).backendUrl).toBe(MOUNTED);
    expect(resolveOptions({ backendUrl: MOUNTED + "/", clientKey: "k" }).backendUrl).toBe(MOUNTED);
    expect(resolveOptions({ backendUrl: "https://chat.biatec.io/", clientKey: "k" }).backendUrl).toBe("https://chat.biatec.io");
  });

  it("puts the prefix in front of the probe, session, popup and keepalive routes, credentialed", async () => {
    const r = recorder((url) => new Response(JSON.stringify(url.endsWith("/session/probe") ? { cookieReturned: true } : { authRequestId: "r1", loginUrl: MOUNTED + "/auth/login?req=r1&nonce=n" }), { status: 200 }));

    expect(await probeCookie(r.cfg)).toBe(true);
    const auth = new AuthClient(r.cfg);
    await auth.session();
    expect(await auth.startLogin()).toEqual({ authRequestId: "r1", loginUrl: MOUNTED + "/auth/login?req=r1&nonce=n" });
    await auth.startElevate("sign");
    await auth.status("r1");
    await new CookieTransport(r.cfg).keepalive();

    expect(r.urls).toEqual([
      `${MOUNTED}/session/probe`,
      `${MOUNTED}/session/probe`,
      `${MOUNTED}/session`,
      `${MOUNTED}/auth/popup`,
      `${MOUNTED}/auth/elevate/popup`,
      `${MOUNTED}/auth/popup/r1`,
      `${MOUNTED}/session/keepalive`,
    ]);
    expect(r.credentials.every((c) => c === "include")).toBe(true);
  });

  it("sends chat messages to <prefix>/chat/messages, the backend's own /chat route behind the mount", async () => {
    const r = recorder(() => new Response("", { status: 500 }));

    await expect((async () => { for await (const _ of new CookieTransport(r.cfg).send({ message: "hi" })) { /* drain */ } })()).rejects.toThrow();

    expect(r.urls).toEqual([`${MOUNTED}/chat/messages`]);
  });

  it("builds the evidence download link under the prefix", () => {
    expect(evidenceDownloadUrl(MOUNTED)).toBe(`${MOUNTED}${PATHS.evidence}`);
    expect(evidenceDownloadUrl(MOUNTED + "/")).toBe(`${MOUNTED}${PATHS.evidence}`);
  });

  it("keeps the backend on the page's origin (no cross-origin call)", () => {
    expect(new URL(MOUNTED).origin).toBe("https://beta.dex.biatec.io");
    expect(new URL(`${MOUNTED}${PATHS.cookieChat}`).origin).toBe(new URL("https://beta.dex.biatec.io/markets").origin);
  });
});
