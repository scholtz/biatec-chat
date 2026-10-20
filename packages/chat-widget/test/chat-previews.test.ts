import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import type { ActionPreview, ActionPreviewStatus, ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { sharedCore, type WidgetServices } from "../src/api/core";
import { PreviewClient, type ConfirmResult, type PreviewApi } from "../src/components/chat/previews";
import { resolveOptions } from "../src/options";

const base = { backendUrl: "https://chat.biatec.io", clientKey: "k" };
const authed: SessionState = {
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  scopes: ["openid"],
  expiresAt: "2030-01-01T00:00:00Z",
};

function preview(status: ActionPreviewStatus = "AwaitingConfirmation", over: Partial<ActionPreview> = {}): ActionPreview {
  return {
    previewId: "p1",
    capability: "swap",
    intent: "Swap 100 USDC for ALGO",
    network: "algorand-testnet",
    genesisId: "testnet-v1.0",
    status,
    expectsAuthenticatorApproval: true,
    expiresAt: new Date(Date.now() + 1800_000).toISOString(),
    steps: [
      {
        stepId: "s1",
        index: 0,
        title: "Swap on pool",
        status,
        groupHash: "HASH-SHOWN",
        assets: [{ assetId: "31566704", symbol: "USDC", amount: "100", decimals: 6 }],
        feeAmount: "0.002 ALGO",
      },
    ],
    ...over,
  };
}
const card = (p: ActionPreview): ChatEvent => ({ type: "preview_card", preview: p });
const result = (status: string, over: Partial<ConfirmResult> = {}): ConfirmResult => ({
  status,
  message: "",
  txIds: [],
  consent: null,
  reauthorizeScope: null,
  raw: { status },
  ...over,
});

function setup(events: ChatEvent[], confirms: ConfirmResult[], extra: Partial<WidgetServices> = {}) {
  const confirm = vi.fn(async (_id: string, _hash: string) => confirms.shift() ?? result("failed"));
  const cancelPreview = vi.fn(async (_id: string, _hash: string) => true);
  const keepalive = { hold: vi.fn(), release: vi.fn() };
  const popup = { location: { href: "" }, closed: false, close: vi.fn() } as unknown as Window;
  const svc: WidgetServices = {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {
        for (const e of events) yield e;
      }),
      dispose: vi.fn(),
    },
    auth: {
      session: vi.fn(async () => authed),
      startLogin: vi.fn(async () => null),
      startElevate: vi.fn(async () => ({ authRequestId: "r1", loginUrl: "https://oidc.example/login" })),
      status: vi.fn(async () => "Complete" as const),
      logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
    },
    pollMs: 1,
    timeoutMs: 100,
    openPopup: () => popup,
    previews: { confirm } satisfies PreviewApi,
    cancelPreview,
    keepalive,
    ...extra,
  };
  return { svc, confirm, cancelPreview, keepalive };
}

let root: VueWrapper | null = null;
async function chat(svc: WidgetServices) {
  root = mount(BiatecChatRoot, { props: { options: resolveOptions(base), services: svc }, attachTo: document.body });
  await root.get(".bc-launcher").trigger("click");
  await flushPromises();
  await root.get("textarea").setValue("swap");
  await root.get("[data-testid=send]").trigger("click");
  await flushPromises();
  return root;
}

/** Review, tick the acknowledgement and press Confirm, like the user does. */
async function confirmClick(w: VueWrapper) {
  await w.get("[data-test=review]").trigger("click");
  document.body.querySelector<HTMLInputElement>(".bc-check input")!.click();
  await flushPromises();
  await w.get("[data-test=confirm]").trigger("click");
  await flushPromises();
}

afterEach(() => {
  root?.unmount();
  root = null;
  document.body.innerHTML = "";
  vi.unstubAllGlobals();
});

describe("preview cards in the conversation", () => {
  it("renders a preview_card and updates it in place on a later event for the same preview", async () => {
    const { svc } = setup(
      [card(preview()), { type: "consent_status", consent: { previewId: "p1", stepId: "s1", state: "Pending", bindingCode: "4821" } }, card(preview("Cancelled"))],
      [],
    );
    const w = await chat(svc);
    expect(w.findAll("article.bc-card")).toHaveLength(1);
    expect(w.get("article.bc-card").attributes("data-status")).toBe("cancelled");
    expect(w.text()).toContain("Swap 100 USDC for ALGO");
  });

  it("shows the card while the reply is still streaming only once per previewId across events", async () => {
    const { svc } = setup([card(preview()), card(preview())], []);
    const w = await chat(svc);
    expect(w.findAll("article.bc-card")).toHaveLength(1);
    expect(w.find("[data-test=confirm]").exists()).toBe(true);
  });

  it("confirm posts the shown group hash, locks while in flight, and landed shows the receipt", async () => {
    let release!: (r: ConfirmResult) => void;
    const { svc, confirm } = setup([card(preview())], []);
    confirm.mockImplementationOnce(() => new Promise<ConfirmResult>((r) => (release = r)));
    const w = await chat(svc);
    await confirmClick(w);
    expect(confirm).toHaveBeenCalledWith("p1", "HASH-SHOWN");
    expect(w.get("[data-test=confirm]").attributes("disabled")).toBeDefined();
    release(result("landed", { txIds: ["TX123"] }));
    await flushPromises();
    expect(w.find("[data-test=confirm]").exists()).toBe(false);
    expect(w.get("[data-test=tx-id]").text()).toBe("TX123");
    expect(w.get("article.bc-card").attributes("aria-label")).toBe("Receipt");
    // Activity shows the confirmation.
    expect(sharedCore()!.activity.entries.some((e) => e.title === "confirmed")).toBe(true);
  });

  it("confirming one step of a multi-step preview settles only that step and keeps the card open", async () => {
    const two = preview("AwaitingConfirmation", {
      steps: [
        { ...preview().steps[0]!, stepId: "s1", index: 0, groupHash: "H1" },
        { ...preview().steps[0]!, stepId: "s2", index: 1, groupHash: "H2" },
      ],
    });
    const { svc, confirm } = setup([card(two)], [result("landed", { txIds: ["TX1"] }), result("landed", { txIds: ["TX2"] })]);
    const w = await chat(svc);
    await confirmClick(w);
    expect(confirm).toHaveBeenLastCalledWith("p1", "H1");
    expect(w.get("article.bc-card").attributes("data-status")).toBe("awaitingConfirmation");
    expect(w.find("[data-test=review]").exists()).toBe(true);
    expect(w.text()).not.toContain("TX2");
    await confirmClick(w);
    expect(confirm).toHaveBeenLastCalledWith("p1", "H2");
    expect(w.get("article.bc-card").attributes("aria-label")).toBe("Receipt");
    expect(w.text()).toContain("TX1");
    expect(w.text()).toContain("TX2");
  });

  it("a double click on Cancel posts one cancel", async () => {
    const { svc, cancelPreview } = setup([card(preview())], []);
    let release!: (v: boolean) => void;
    cancelPreview.mockImplementationOnce(() => new Promise<boolean>((r) => (release = r)));
    const w = await chat(svc);
    const cancel = w.findAll("button").find((b) => b.text() === "Cancel")!;
    await cancel.trigger("click");
    await cancel.trigger("click");
    release(true);
    await flushPromises();
    expect(cancelPreview).toHaveBeenCalledTimes(1);
    expect(w.find(".bc-preview-note").exists()).toBe(false);
    expect(w.get("article.bc-card").attributes("data-status")).toBe("cancelled");
  });

  it("submitted also shows the receipt", async () => {
    const { svc } = setup([card(preview())], [result("submitted", { txIds: ["TX9"] })]);
    const w = await chat(svc);
    await confirmClick(w);
    expect(w.get("[data-test=tx-id]").text()).toBe("TX9");
  });

  it("consent_pending shows the phone state with the binding code", async () => {
    const { svc } = setup(
      [card(preview())],
      [result("consent_pending", { consent: { previewId: "p1", stepId: "s1", state: "Pending", bindingCode: "4821" } })],
    );
    const w = await chat(svc);
    await confirmClick(w);
    expect(w.find("[data-test=consent]").exists()).toBe(true);
    expect(w.get("[data-test=binding-code]").text()).toBe("4821");
  });

  it("scope_required opens the elevate popup and retries the confirm once it completes", async () => {
    const { svc, confirm } = setup([card(preview())], [result("scope_required", { reauthorizeScope: "sign" }), result("landed", { txIds: ["T1"] })]);
    const w = await chat(svc);
    await confirmClick(w);
    await vi.waitFor(() => expect(confirm).toHaveBeenCalledTimes(2));
    await flushPromises();
    expect(svc.auth.startElevate).toHaveBeenCalledWith("sign");
    expect(w.get("[data-test=tx-id]").text()).toBe("T1");
  });

  it("scope_required retries only once", async () => {
    const { svc, confirm } = setup([card(preview())], [result("scope_required"), result("scope_required"), result("scope_required")]);
    const w = await chat(svc);
    await confirmClick(w);
    // The retry runs after the elevate popup is polled on a real timer, so wait for it instead of one flush.
    await vi.waitFor(() => expect(confirm).toHaveBeenCalledTimes(2));
    await vi.waitFor(() => expect(w.find("[data-test=scope]").exists()).toBe(true));
    // And it never retries a second time.
    await new Promise((r) => setTimeout(r, 50));
    await flushPromises();
    expect(confirm).toHaveBeenCalledTimes(2);
  });

  it.each([
    ["agent_paused", "refusal-resume"],
    ["agent_analyze_only", "refusal-change-mode"],
  ])("%s shows the refusal with its way out and no Activity confirmation", async (code, button) => {
    const { svc } = setup([card(preview())], [result(code)]);
    const w = await chat(svc);
    await confirmClick(w);
    expect(w.get("[data-testid=agent-refusal]").attributes("data-code")).toBe(code);
    expect(w.find(`[data-testid=${button}]`).exists()).toBe(true);
    expect(sharedCore()!.activity.entries.some((e) => e.title === "confirmed")).toBe(false);
    // The card stays open: nothing was confirmed.
    expect(w.find("[data-test=confirm]").exists()).toBe(true);
  });

  it("sign_in_required, sign_in_unavailable and failed show a note and unlock Confirm", async () => {
    for (const [status, text] of [
      ["sign_in_required", "Sign in again"],
      ["sign_in_unavailable", "Signing is not available"],
      ["failed", "Pool moved"],
    ] as const) {
      const { svc } = setup([card(preview())], [result(status, { message: "Pool moved" })]);
      const w = await chat(svc);
      await confirmClick(w);
      expect(w.get(".bc-preview-note").text()).toContain(text);
      expect(w.get("[data-test=confirm]").attributes("disabled")).toBeUndefined();
      w.unmount();
      root = null;
      document.body.innerHTML = "";
    }
  });

  it("a network error on confirm shows a note and nothing is confirmed", async () => {
    const { svc, confirm } = setup([card(preview())], []);
    confirm.mockRejectedValueOnce(new Error("offline"));
    const w = await chat(svc);
    await confirmClick(w);
    expect(w.get(".bc-preview-note").text()).toContain("Nothing was signed");
    expect(sharedCore()!.activity.entries.some((e) => e.title === "confirmed")).toBe(false);
  });

  it("cancel posts the group hash and the card shows Cancelled", async () => {
    const { svc, cancelPreview } = setup([card(preview())], []);
    const w = await chat(svc);
    const cancel = w.findAll("button").find((b) => b.text() === "Cancel")!;
    await cancel.trigger("click");
    await flushPromises();
    expect(cancelPreview).toHaveBeenCalledWith("p1", "HASH-SHOWN");
    expect(w.get("article.bc-card").attributes("data-status")).toBe("cancelled");
    expect(w.text()).toContain("Cancelled");
  });

  it("cancelling step 2 after step 1 landed keeps step 1 settled", async () => {
    const two = preview("AwaitingConfirmation", {
      steps: [
        { ...preview().steps[0]!, stepId: "s1", index: 0, groupHash: "H1" },
        { ...preview().steps[0]!, stepId: "s2", index: 1, groupHash: "H2" },
      ],
    });
    const { svc, cancelPreview } = setup([card(two)], [result("landed", { txIds: ["TX1"] })]);
    const w = await chat(svc);
    await confirmClick(w);
    const cancel = w.findAll("button").find((b) => b.text() === "Cancel")!;
    await cancel.trigger("click");
    await flushPromises();
    expect(cancelPreview).toHaveBeenCalledWith("p1", "H2");
    expect(w.get("article.bc-card").attributes("data-status")).toBe("completed");
    expect(w.text()).toContain("TX1");
  });

  it("a refused cancel leaves the card open with a note", async () => {
    const { svc, cancelPreview } = setup([card(preview())], []);
    cancelPreview.mockResolvedValueOnce(false);
    const w = await chat(svc);
    await w.findAll("button").find((b) => b.text() === "Cancel")!.trigger("click");
    await flushPromises();
    expect(w.get(".bc-preview-note").text()).toContain("could not be cancelled");
    expect(w.get("article.bc-card").attributes("data-status")).toBe("awaitingConfirmation");
  });

  it("keeps the session alive while a preview is pending and releases it when finished", async () => {
    const { svc, keepalive } = setup([card(preview()), card(preview("Completed"))], []);
    await chat(svc);
    expect(keepalive.hold).toHaveBeenCalledWith("preview:p1");
    expect(keepalive.release).toHaveBeenCalledWith("preview:p1");
  });

  it("holds the keepalive for a pending consent", async () => {
    const { svc, keepalive } = setup([card(preview("AwaitingApproval"))], []);
    await chat(svc);
    expect(keepalive.hold).toHaveBeenCalledWith("preview:p1");
    expect(keepalive.release).not.toHaveBeenCalled();
  });
});

describe("PreviewClient", () => {
  it("posts { groupHash } with the CSRF and client key headers and parses the ConfirmResponse", async () => {
    const f = vi.fn(async (_u: string, _i: RequestInit) =>
      new Response(JSON.stringify({ status: "scope_required", message: "m", txIds: [], reauthorizeScope: "sign" }), { status: 403 }),
    );
    const c = new PreviewClient({ ...base, fetch: f as unknown as typeof fetch });
    const r = await c.confirm("a b", "H");
    expect(f.mock.calls[0]![0]).toBe("https://chat.biatec.io/chat/previews/a%20b/confirm");
    const init = f.mock.calls[0]![1];
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("include");
    expect(JSON.parse(init.body as string)).toEqual({ groupHash: "H" });
    expect((init.headers as Record<string, string>)["X-Biatec-Chat"]).toBe("1");
    expect(r.status).toBe("scope_required");
    expect(r.reauthorizeScope).toBe("sign");
  });

  it("maps a bare 401 to sign_in_required and an error body to a failure", async () => {
    const mk = (body: unknown, status: number) =>
      new PreviewClient({ ...base, fetch: (async () => new Response(body === null ? "" : JSON.stringify(body), { status })) as unknown as typeof fetch });
    expect((await mk(null, 401).confirm("p", "h")).status).toBe("sign_in_required");
    const gone = await mk({ error: "Expired", message: "Preview expired." }, 410).confirm("p", "h");
    expect(gone.status).toBe("error:Expired");
    expect(gone.message).toBe("Preview expired.");
  });
});
