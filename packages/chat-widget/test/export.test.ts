import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { ConversationSummary, ErasureResponse, SessionState } from "@biatec/chat-protocol";
import ExportView from "../src/components/export/ExportView.vue";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { RetentionClient, RetentionError, RETENTION_PATHS, type RetentionApi } from "../src/components/export/api";
import { resolveOptions } from "../src/options";
import { resetHostApi } from "../src/api";
import type { WidgetServices } from "../src/api/core";
import { CLIENT_KEY_HEADER, CSRF_HEADER } from "../src/transport/types";

const fixtures = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "evidence-verifier", "test", "fixtures");
const bundleText = readFileSync(join(fixtures, "bundle.json"), "utf8");
const jwks: unknown = JSON.parse(readFileSync(join(fixtures, "jwks.json"), "utf8"));

// The offline verifier and the bundle the real backend exported (EvidenceExportTests), both from evidence-verifier.
// Loaded at run time so the widget's typecheck (ES2022 lib) does not compile the verifier's sources.
type Verify = (bundle: unknown, options: { jwks: unknown }) => { valid: boolean; errors: string[]; entries: number };
const verifierUrl = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "evidence-verifier", "src", "verify.ts");
const { verifyBundle } = (await import(/* @vite-ignore */ verifierUrl)) as { verifyBundle: Verify };
const bundleId = (JSON.parse(bundleText) as { conversationId: string }).conversationId;

const OLD = bundleId;
const OTHER = "0b8f8a43-6f6a-4c61-9d36-3f3f7c0d2a11";
const NEW = "8a1d2c3e-1111-4222-8333-944455556666";

const conv = (id: string, current: boolean, evidenceEntries: number | string = 3): ConversationSummary => ({
  conversationId: id,
  createdAt: "2026-10-19T08:30:00Z",
  lastMessageAt: "2026-10-19T09:00:00Z",
  current,
  evidenceEntries,
});

/** Fake backend for the export view: GET list, GET evidence (the fixture bundle) and POST erase, with a call log. */
function fakeApi(opts: { erase?: () => Promise<ErasureResponse> } = {}) {
  let list = [conv(OLD, true), conv(OTHER, false, "0")];
  const calls: string[] = [];
  const api: RetentionApi = {
    list: vi.fn(async () => {
      calls.push("list");
      return list;
    }),
    evidence: vi.fn(async (id: string) => {
      calls.push(`evidence:${id}`);
      return { name: `biatec-evidence-${id.replace(/-/g, "")}.json`, blob: new Blob([bundleText], { type: "application/json" }) };
    }),
    erase: vi.fn(async () => {
      calls.push("erase");
      const r = opts.erase ? await opts.erase() : { status: "erased", conversations: 2, payloadsDeleted: 5, conversationId: NEW };
      // The backend lists the fresh conversation as the session's own; the erased ones stay (blanked).
      list = [conv(NEW, true, 0), conv(OLD, false), conv(OTHER, false, 0)];
      return r;
    }),
  };
  return { api, calls };
}

/** jsdom's Blob has no text(); FileReader reads it. */
const readBlob = (b: Blob) =>
  new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => reject(r.error);
    r.readAsText(b);
  });

/** Captures what the widget hands to the browser's save flow. */
let saved: { name: string; blob: Blob }[] = [];
beforeEach(() => {
  saved = [];
  const blobs = new Map<string, Blob>();
  let n = 0;
  vi.stubGlobal("URL", Object.assign(URL, {
    createObjectURL: vi.fn((b: Blob) => {
      const u = `blob:test/${++n}`;
      blobs.set(u, b);
      return u;
    }),
    revokeObjectURL: vi.fn(),
  }));
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    saved.push({ name: this.download, blob: blobs.get(this.getAttribute("href")!)! });
  });
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  resetHostApi();
});

const mountView = (api: RetentionApi, signedIn = true) => mount(ExportView, { props: { api, signedIn }, attachTo: document.body });

describe("export view", () => {
  it("lists the user's conversations, marks the current one and states the receipts carve-out", async () => {
    const { api } = fakeApi();
    const w = mountView(api);
    await flushPromises();
    const items = w.findAll("[data-testid=export-conversation]");
    expect(items.map((i) => i.attributes("data-id"))).toEqual([OLD, OTHER]);
    expect(items[0]!.attributes("data-current")).toBe("");
    expect(items[1]!.attributes("data-current")).toBeUndefined();
    expect(items[0]!.text()).toContain("Evidence entries: 3");
    // No evidence yet: no download button for that one.
    expect(items[1]!.find("[data-testid=export-download]").exists()).toBe(false);
    expect(items[1]!.text()).toContain("No evidence yet");
    expect(w.get("[data-testid=export-carve-out]").text()).toMatch(/Receipts and transaction references are kept.*financial record-keeping/);
  });

  it("downloads the conversation's bundle, and the saved file verifies offline against the JWKS", async () => {
    const { api, calls } = fakeApi();
    const w = mountView(api);
    await flushPromises();
    await w.get("[data-testid=export-download]").trigger("click");
    await flushPromises();
    expect(calls).toContain(`evidence:${OLD}`);
    expect(saved).toHaveLength(1);
    expect(saved[0]!.name).toBe(`biatec-evidence-${OLD.replace(/-/g, "")}.json`);
    const r = verifyBundle(JSON.parse(await readBlob(saved[0]!.blob)), { jwks });
    expect(r.errors).toEqual([]);
    expect(r.valid).toBe(true);
    expect(r.entries).toBe(3);
    // The object URL is released after the save.
    await new Promise((res) => setTimeout(res, 0));
    expect(URL.revokeObjectURL).toHaveBeenCalled();
  });

  it("shows a retry when the list cannot be loaded", async () => {
    const { api } = fakeApi();
    vi.mocked(api.list).mockRejectedValueOnce(new RetentionError("failed", 500));
    const w = mountView(api);
    await flushPromises();
    expect(w.get("[data-testid=export-load-failed]").attributes("role")).toBe("alert");
    await w.get("[data-testid=export-retry]").trigger("click");
    await flushPromises();
    expect(w.findAll("[data-testid=export-conversation]")).toHaveLength(2);
  });

  it("renders backend values as text, never as markup", async () => {
    const { api } = fakeApi();
    vi.mocked(api.list).mockResolvedValueOnce([{ ...conv(OLD, true), createdAt: "<img src=x onerror=alert(1)>" }]);
    const w = mountView(api);
    await flushPromises();
    expect(w.find("img").exists()).toBe(false);
    expect(w.text()).toContain("<img src=x onerror=alert(1)>");
  });
});

describe("erase", () => {
  it("a guest gets the 30-day note instead of the erase button", async () => {
    const { api } = fakeApi();
    const w = mountView(api, false);
    await flushPromises();
    expect(w.find("[data-testid=export-erase]").exists()).toBe(false);
    expect(w.get("[data-testid=export-erase-guest]").text()).toContain("30 days");
  });

  it("asks for confirmation, offers the export first, then switches to the new conversation id", async () => {
    const { api, calls } = fakeApi();
    const w = mountView(api);
    await flushPromises();
    await w.get("[data-testid=export-erase]").trigger("click");
    await flushPromises();
    // Nothing is erased by opening the confirm step.
    expect(calls).toEqual(["list"]);
    const dialog = document.body.querySelector("[role=dialog]")!;
    expect(dialog.textContent).toContain("Erase your conversations?");
    expect(dialog.textContent).toContain("financial record-keeping");

    // Export first: every conversation that has evidence, one file each.
    await w.get("[data-testid=erase-export-first]").trigger("click");
    await flushPromises();
    expect(calls).toEqual(["list", `evidence:${OLD}`]);
    expect(saved).toHaveLength(1);
    expect(w.get("[data-testid=erase-export-first]").text()).toContain("Evidence downloaded");

    await w.get("[data-testid=erase-confirm-button]").trigger("click");
    await flushPromises();
    expect(calls).toEqual(["list", `evidence:${OLD}`, "erase", "list"]);
    expect(w.emitted("erased")).toEqual([[NEW]]);
    expect(document.body.querySelector("[role=dialog]")).toBeNull();
    const current = w.findAll("[data-testid=export-conversation]").filter((i) => i.attributes("data-current") === "");
    expect(current.map((i) => i.attributes("data-id"))).toEqual([NEW]);
    expect(w.get("[data-testid=export-status]").text()).toContain("A new conversation has started");
  });

  it("Cancel closes the confirm step without erasing", async () => {
    const { api, calls } = fakeApi();
    const w = mountView(api);
    await flushPromises();
    await w.get("[data-testid=export-erase]").trigger("click");
    await w.get("[data-testid=erase-cancel]").trigger("click");
    await flushPromises();
    expect(document.body.querySelector("[role=dialog]")).toBeNull();
    expect(calls).toEqual(["list"]);
    expect(w.emitted("erased")).toBeUndefined();
  });

  it("409 ActionInFlight shows 'finish or cancel the action first' and keeps the conversation", async () => {
    const { api } = fakeApi({ erase: () => Promise.reject(new RetentionError("action_in_flight", 409)) });
    const w = mountView(api);
    await flushPromises();
    await w.get("[data-testid=export-erase]").trigger("click");
    await w.get("[data-testid=erase-confirm-button]").trigger("click");
    await flushPromises();
    const err = w.get("[data-testid=erase-error]");
    expect(err.attributes("role")).toBe("alert");
    expect(err.attributes("data-code")).toBe("action_in_flight");
    expect(err.text()).toBe("Finish or cancel the action first, then erase your data.");
    expect(document.body.querySelector("[role=dialog]")).not.toBeNull();
    expect(w.emitted("erased")).toBeUndefined();
  });
});

describe("RetentionClient", () => {
  const cfg = (f: typeof fetch) => ({ backendUrl: "https://chat.biatec.io/", clientKey: "k", fetch: f });

  it("lists with the session cookie only; erase adds the CSRF headers; no token anywhere", async () => {
    const seen: { url: string; init: RequestInit }[] = [];
    const f = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
      seen.push({ url: String(url), init: init! });
      return String(url).endsWith(RETENTION_PATHS.erase)
        ? Response.json({ status: "erased", conversations: 1, payloadsDeleted: 2, conversationId: NEW })
        : Response.json({ conversations: [conv(OLD, true)] });
    }) as unknown as typeof fetch;
    const c = new RetentionClient(cfg(f));
    expect((await c.list()).map((x) => x.conversationId)).toEqual([OLD]);
    expect((await c.erase()).conversationId).toBe(NEW);
    expect(seen[0]!.url).toBe("https://chat.biatec.io/session/conversations");
    expect(seen[0]!.init.method).toBe("GET");
    expect(seen[0]!.init.credentials).toBe("include");
    expect(seen[1]!.url).toBe("https://chat.biatec.io/session/erase");
    expect(seen[1]!.init.method).toBe("POST");
    expect(seen[1]!.init.credentials).toBe("include");
    const h = seen[1]!.init.headers as Record<string, string>;
    expect(h[CSRF_HEADER]).toBe("1");
    expect(h[CLIENT_KEY_HEADER]).toBe("k");
    for (const s of seen) expect(JSON.stringify(s.init.headers)).not.toMatch(/authorization|bearer/i);
  });

  it("maps 409 ActionInFlight, 401 and 404 to typed errors", async () => {
    const answer = (status: number, body: unknown) => (async () => Response.json(body, { status })) as unknown as typeof fetch;
    await expect(new RetentionClient(cfg(answer(409, { error: "ActionInFlight" }))).erase()).rejects.toMatchObject({ code: "action_in_flight" });
    await expect(new RetentionClient(cfg(answer(401, {}))).erase()).rejects.toMatchObject({ code: "unauthorized" });
    await expect(new RetentionClient(cfg(answer(404, {}))).evidence(OLD)).rejects.toMatchObject({ code: "not_found" });
  });

  it("downloads the bundle bytes unchanged under the Content-Disposition file name", async () => {
    const f = (async () =>
      new Response(bundleText, {
        headers: { "Content-Type": "application/json", "Content-Disposition": `attachment; filename="biatec-evidence-abc.json"` },
      })) as unknown as typeof fetch;
    const file = await new RetentionClient(cfg(f)).evidence(OLD);
    expect(file.name).toBe("biatec-evidence-abc.json");
    expect(await file.blob.text()).toBe(bundleText);
  });
});

describe("mounted in the widget", () => {
  const authed: SessionState = {
    sessionId: "s",
    tier: "Authenticated",
    email: "alice@example.com",
    scopes: ["openid"],
    expiresAt: "2030-01-01T00:00:00Z",
    agentMode: "prepare",
    paused: false,
  };

  it("lives in the Activity sheet, and an erase clears the visible conversation", async () => {
    const { api } = fakeApi();
    const svc: WidgetServices = {
      transport: {
        open: vi.fn(async () => "success" as const),
        retryOnSignIn: vi.fn(async () => true),
        send: vi.fn(async function* () {
          yield { type: "text_delta" as const, text: "Hello" };
        }),
        dispose: vi.fn(),
      },
      auth: {
        session: vi.fn(async () => authed),
        startLogin: vi.fn(async () => null),
        startElevate: vi.fn(async () => null),
        status: vi.fn(async () => "Complete" as const),
        logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
      },
      retention: api,
    };
    const w = mount(BiatecChatRoot, {
      props: { options: resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k" }), services: svc },
      attachTo: document.body,
    });
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    await w.get(".bc-panel textarea").setValue("hi");
    await w.get("form").trigger("submit");
    await flushPromises();
    expect(w.findAll(".bc-msg")).toHaveLength(2);

    await w.get("[data-testid=activity-toggle]").trigger("click");
    await flushPromises();
    expect(w.find(".bc-shell-aside [data-testid=export]").exists()).toBe(true);
    await w.get("[data-testid=export-erase]").trigger("click");
    await w.get("[data-testid=erase-confirm-button]").trigger("click");
    await flushPromises();
    expect(api.erase).toHaveBeenCalledTimes(1);
    expect(w.findAll(".bc-msg")).toHaveLength(0);
    w.unmount();
  });
});
