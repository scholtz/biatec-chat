import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import { nextTick } from "vue";
import type { ActionPreview, AgentMode, ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { resolveOptions } from "../src/options";
import type { WidgetServices } from "../src/api/core";
import { resetHostApi } from "../src/api";
import { AgentClient, refusalCode } from "../src/components/agent/api";
import { groupByDay, useActivity } from "../src/components/activity/model";
import { t } from "../src/i18n";

const opts = resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k" });

afterEach(() => {
  document.body.innerHTML = "";
  resetHostApi();
});

const anon: SessionState = { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" };
const authed = (agentMode: AgentMode = "prepare", paused = false): SessionState => ({
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  primaryAddress: "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
  scopes: ["openid", "sign"],
  expiresAt: "2030-01-01T00:00:00Z",
  agentMode,
  paused,
});

function preview(id: string, status: ActionPreview["status"] = "AwaitingConfirmation", intent = `Swap 100 USDC (${id})`): ActionPreview {
  return {
    previewId: id,
    capability: "dex.swap",
    intent,
    network: "algorand-testnet",
    genesisId: "testnet-v1.0",
    status,
    expectsAuthenticatorApproval: false,
    limit: { bucket: "Daily", remaining: "900", currency: "USD" },
    expiresAt: "2030-01-01T00:00:00Z",
    steps: [{ stepId: "s1", index: 0, title: "Swap", status, groupHash: `hash-${id}` }],
  } as ActionPreview;
}

/** Backend double: GET /session, PUT /session/agent, preview cancel and the sign-out popup, with a call log. */
function backend(start: SessionState, streams: ChatEvent[][] = []) {
  let session = start;
  const calls: string[] = [];
  const popup = { closed: false, close: vi.fn(), location: { href: "" } };
  const svc: WidgetServices = {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {
        for (const e of streams.shift() ?? []) yield e;
      }),
      dispose: vi.fn(),
    },
    auth: {
      session: vi.fn(async () => session),
      startLogin: vi.fn(async () => null),
      startElevate: vi.fn(async () => null),
      status: vi.fn(async () => "Complete" as const),
      logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
    },
    agent: {
      setAgent: vi.fn(async (mode: AgentMode, paused: boolean) => {
        calls.push(`put:${mode}:${paused}`);
        session = { ...session, agentMode: mode, paused };
        return { agentMode: mode, paused };
      }),
      cancelPreview: vi.fn(async (id: string, hash: string) => {
        calls.push(`cancel:${id}:${hash}`);
        return true;
      }),
    },
    openPopup: () => {
      calls.push("signout");
      // The backend logout ends the session.
      session = anon;
      return popup as unknown as Window;
    },
    pollMs: 1,
    timeoutMs: 100,
  };
  return { svc, calls, reload: (s: SessionState) => (session = s) };
}

async function openPanel(svc: WidgetServices) {
  const w = mount(BiatecChatRoot, { props: { options: opts, services: svc }, attachTo: document.body });
  await w.get(".bc-launcher").trigger("click");
  await flushPromises();
  return w;
}
const pill = (w: Awaited<ReturnType<typeof openPanel>>) => w.get("[data-testid=status-pill]");
async function send(w: Awaited<ReturnType<typeof openPanel>>, text: string) {
  await w.get(".bc-panel textarea").setValue(text);
  await w.get("form").trigger("submit");
  await flushPromises();
}

describe("permissions dialog: lowering is instant, raising needs the confirm step", () => {
  it("prepare → analyze and Pause call the API at once; the pill follows the server", async () => {
    const b = backend(authed("prepare"));
    const w = await openPanel(b.svc);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Prepare for approval");
    await pill(w).trigger("click");
    await w.get("[data-testid=mode-analyze]").setValue(true);
    await flushPromises();
    expect(b.calls).toEqual(["put:analyze:false"]);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Analyze only");
    // Still in the dialog, no confirm step for lowering.
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("edit");

    await w.get("[data-testid=pause]").trigger("click");
    await flushPromises();
    expect(b.calls).toEqual(["put:analyze:false", "put:analyze:true"]);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Paused");
    expect(w.get(".bc-status-dot").attributes("data-mode")).toBe("paused");
    w.unmount();
  });

  it("analyze → prepare goes through Continue and a confirm step disabled until the checkbox is ticked", async () => {
    const b = backend(authed("analyze"));
    const w = await openPanel(b.svc);
    await pill(w).trigger("click");
    const cont = () => w.get("[data-testid=perm-continue]");
    expect(cont().attributes("disabled")).toBeDefined();
    await w.get("[data-testid=mode-prepare]").setValue(true);
    await flushPromises();
    expect(b.calls).toEqual([]);
    expect(cont().attributes("disabled")).toBeUndefined();
    await cont().trigger("click");
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("confirm");
    expect(w.text()).toContain("Give Biatec AI more autonomy?");
    expect(w.get("[data-testid=powers]").findAll("li").length).toBeGreaterThan(1);
    const confirm = () => w.get("[data-testid=confirm-increase]");
    expect(confirm().attributes("disabled")).toBeDefined();
    await confirm().trigger("click");
    expect(b.calls).toEqual([]);
    await w.get("[data-testid=ack]").setValue(true);
    expect(confirm().attributes("disabled")).toBeUndefined();
    await confirm().trigger("click");
    await flushPromises();
    expect(b.calls).toEqual(["put:prepare:false"]);
    expect(w.find("[data-testid=permissions-dialog]").exists()).toBe(false);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Prepare for approval");
    w.unmount();
  });

  it("Resume from the paused banner shows the confirm step; the banner goes once resumed", async () => {
    const b = backend(authed("prepare", true));
    const w = await openPanel(b.svc);
    expect(w.get("[data-testid=paused-banner]").text()).toContain("Biatec AI is paused");
    await w.get("[data-testid=banner-resume]").trigger("click");
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("confirm");
    expect(w.get("[data-testid=confirm-increase]").attributes("disabled")).toBeDefined();
    await w.get("[data-testid=ack]").setValue(true);
    await w.get("[data-testid=confirm-increase]").trigger("click");
    await flushPromises();
    expect(b.calls).toEqual(["put:prepare:false"]);
    expect(w.find("[data-testid=paused-banner]").exists()).toBe(false);
    w.unmount();
  });

  it("the pill shows the server state after a reload (GET /session)", async () => {
    const b = backend(authed("analyze", true));
    const w = await openPanel(b.svc);
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Paused");
    w.unmount();
    const b2 = backend(authed("analyze", false));
    const w2 = await openPanel(b2.svc);
    expect(w2.get("[data-testid=agent-mode]").text()).toBe("Analyze only");
    w2.unmount();
  });

  it("a failed save leaves the pill as it was and says so", async () => {
    const b = backend(authed("prepare"));
    b.svc.agent!.setAgent = vi.fn(async () => {
      throw new Error("offline");
    });
    const w = await openPanel(b.svc);
    await pill(w).trigger("click");
    await w.get("[data-testid=mode-analyze]").setValue(true);
    await flushPromises();
    expect(w.get("[role=alert]").text()).toBe(t("agent.saveFailed"));
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Prepare for approval");
    expect((w.get("[data-testid=mode-prepare]").element as HTMLInputElement).checked).toBe(true);
    w.unmount();
  });

  it("Show my spending limits asks through the chat; the safeguard note is shown", async () => {
    const b = backend(authed());
    const w = await openPanel(b.svc);
    await pill(w).trigger("click");
    expect(w.text()).toContain("Limits are safeguards, not guarantees against loss.");
    await w.get("[data-testid=show-limits]").trigger("click");
    await flushPromises();
    expect(b.svc.transport.send).toHaveBeenCalledWith(expect.objectContaining({ message: "What are my current spending limits?" }));
    expect(w.find("[data-testid=permissions-dialog]").exists()).toBe(false);
    w.unmount();
  });

  it("Guest sees Analyze only selected and a sign-in call to action instead of the other modes", async () => {
    const b = backend(anon);
    const w = await openPanel(b.svc);
    await pill(w).trigger("click");
    const analyze = w.get("[data-testid=mode-analyze]").element as HTMLInputElement;
    expect(analyze.checked).toBe(true);
    expect(w.find("[data-testid=mode-prepare]").exists()).toBe(false);
    expect(w.find("[data-testid=mode-rules]").exists()).toBe(false);
    expect(w.find("[data-testid=pause-row]").exists()).toBe(false);
    expect(w.find("[data-testid=revoke]").exists()).toBe(false);
    expect(w.find("[data-testid=show-limits]").exists()).toBe(false);
    expect(w.find("[data-testid=guest-sign-in]").exists()).toBe(true);
    w.unmount();
  });

  it("Rule-based automation is listed as not available yet and cannot be chosen", async () => {
    const w = await openPanel(backend(authed()).svc);
    await pill(w).trigger("click");
    expect((w.get("[data-testid=mode-rules]").element as HTMLInputElement).disabled).toBe(true);
    expect(w.text()).toContain("Not available yet");
    expect(w.get("[role=radiogroup]").findAll("input[type=radio]").length).toBe(3);
    w.unmount();
  });
});

describe("Revoke all access", () => {
  it("cancels every open preview (decision calls) before signing out, then the header is Guest", async () => {
    const b = backend(authed(), [
      [
        { type: "preview_card", preview: preview("p1") },
        { type: "preview_card", preview: preview("p2") },
        { type: "preview_card", preview: preview("p3", "Completed") },
        { type: "text_delta", text: "Here are two previews." },
      ],
    ]);
    const w = await openPanel(b.svc);
    await send(w, "swap twice");
    await pill(w).trigger("click");
    await w.get("[data-testid=revoke]").trigger("click");
    expect(w.get("[data-testid=revoke-count]").text()).toContain("2");
    await w.get("[data-testid=revoke-confirm]").trigger("click");
    await flushPromises();
    await new Promise((r) => setTimeout(r, 20));
    await flushPromises();
    expect(b.calls).toEqual(["cancel:p1:hash-p1", "cancel:p2:hash-p2", "signout"]);
    expect(w.get("[data-testid=tier]").text()).toBe("Guest");
    expect(w.get("[data-testid=agent-mode]").text()).toBe("Analyze only");
    expect(w.text()).toContain(t("agent.toastRevoked"));
    w.unmount();
  });
});

describe("typed agent refusals", () => {
  it("parses confirm responses and error bodies", () => {
    expect(refusalCode({ status: "agent_paused", message: "…" })).toBe("agent_paused");
    expect(refusalCode({ error: "agent_analyze_only" })).toBe("agent_analyze_only");
    expect(refusalCode({ status: "landed" })).toBeNull();
    expect(refusalCode(null)).toBeNull();
  });

  it("agent_paused shows a message with Resume, which opens the confirm step", async () => {
    const b = backend(authed("prepare", true), [
      [{ type: "error", code: "agent_paused", message: "paused", retryable: false }],
    ]);
    const w = await openPanel(b.svc);
    await send(w, "confirm it");
    const notice = w.get("[data-testid=agent-refusal]");
    expect(notice.attributes("role")).toBe("alert");
    expect(notice.text()).toContain(t("agent.refusalPaused"));
    // The refusal takes the banner's place, so the user sees one message.
    expect(w.find("[data-testid=paused-banner]").exists()).toBe(false);
    await w.get("[data-testid=refusal-resume]").trigger("click");
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("confirm");
    w.unmount();
  });

  it("agent_analyze_only shows Change mode, which offers the raise to Prepare", async () => {
    const b = backend(authed("analyze"), [
      [{ type: "error", code: "agent_analyze_only", message: "analyze", retryable: false }],
    ]);
    const w = await openPanel(b.svc);
    await send(w, "confirm it");
    expect(w.get("[data-testid=agent-refusal]").text()).toContain(t("agent.refusalAnalyze"));
    await w.get("[data-testid=refusal-change-mode]").trigger("click");
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("confirm");
    await w.get("[data-testid=ack]").setValue(true);
    await w.get("[data-testid=confirm-increase]").trigger("click");
    await flushPromises();
    expect(b.calls).toEqual(["put:prepare:false"]);
    expect(w.find("[data-testid=agent-refusal]").exists()).toBe(false);
    w.unmount();
  });
});

describe("Activity timeline", () => {
  it("links to the evidence export endpoint", async () => {
    const w = await openPanel(backend(authed("prepare")).svc);
    await w.get("[data-testid=activity-toggle]").trigger("click");
    expect(w.get("[data-testid=activity-evidence]").attributes("href")).toBe("https://chat.biatec.io/session/evidence");
  });

  const stream: ChatEvent[] = [
    { type: "preview_card", preview: preview("p1", "AwaitingConfirmation", "Swap 100 USDC for ALGO") },
    { type: "preview_card", preview: preview("p1", "Signing", "Swap 100 USDC for ALGO") },
    { type: "preview_card", preview: preview("p1", "Completed", "Swap 100 USDC for ALGO") },
    { type: "text_delta", text: "Done." },
  ];

  it("lists a preview, a confirmation, a receipt and a mode change from the event stream, newest first, filterable", async () => {
    const b = backend(authed("prepare"), [stream]);
    const w = await openPanel(b.svc);
    await send(w, "swap 100 USDC");
    await pill(w).trigger("click");
    await w.get("[data-testid=pause]").trigger("click");
    await flushPromises();
    await w.get("[data-testid=perm-cancel]").trigger("click");
    await w.get("[data-testid=activity-toggle]").trigger("click");
    const titles = () => w.findAll("[data-testid=activity-entry] .bc-activity-title span").map((s) => s.text());
    expect(titles()).toEqual([
      "Agent paused",
      "Analysis",
      "Completed · receipt",
      "Confirmed",
      "Preview prepared",
    ]);
    const confirmed = w.findAll("[data-testid=activity-entry]")[3]!;
    expect(confirmed.text()).toContain("by You");
    expect(confirmed.text()).toContain("Swap 100 USDC for ALGO");
    expect(w.findAll("[data-testid=activity-entry]")[4]!.text()).toContain("by Biatec AI");
    expect(w.get(".bc-activity-dayh").text()).toBe("Today");

    await w.get("[data-testid=filter-execution]").trigger("click");
    expect(titles()).toEqual(["Completed · receipt", "Confirmed"]);
    expect(w.get("[data-testid=filter-execution]").attributes("aria-pressed")).toBe("true");
    await w.get("[data-testid=filter-permission]").trigger("click");
    expect(titles()).toEqual(["Agent paused"]);
    await w.get("[data-testid=filter-preview]").trigger("click");
    expect(titles()).toEqual(["Preview prepared"]);
    await w.get("[data-testid=filter-all]").trigger("click");
    expect(titles().length).toBe(5);
    expect(w.text()).toContain("This session only.");
    w.unmount();
  });

  it("Back to chat closes the sheet; the aside's Pause is instant", async () => {
    const b = backend(authed("prepare"));
    const w = await openPanel(b.svc);
    await w.get("[data-testid=activity-toggle]").trigger("click");
    await w.get("[data-testid=activity-pause]").trigger("click");
    await flushPromises();
    expect(b.calls).toEqual(["put:prepare:true"]);
    expect(w.find("[data-testid=activity-resume]").exists()).toBe(true);
    await w.get("[data-testid=activity-back]").trigger("click");
    expect(w.find("[data-testid=activity]").exists()).toBe(false);
    w.unmount();
  });

  it("logs a repeated card once, a consent result, and groups by day", () => {
    let now = Date.parse("2026-10-19T10:00:00");
    const a = useActivity(() => now);
    a.record({ type: "preview_card", preview: preview("p1") });
    a.record({ type: "preview_card", preview: preview("p1") });
    now = Date.parse("2026-10-20T09:00:00");
    a.record({ type: "consent_status", consent: { previewId: "p1", stepId: "s1", state: "Pending" } });
    a.record({ type: "consent_status", consent: { previewId: "p1", stepId: "s1", state: "Denied" } });
    a.recordConfirm("p1");
    a.recordConfirm("p1");
    expect(a.entries.map((e) => e.title)).toEqual(["previewPrepared", "consentDenied", "confirmed"]);
    const days = groupByDay(a.entries, "all");
    expect(days.map((d) => d.day)).toEqual(["2026-10-20", "2026-10-19"]);
    expect(days[0]!.entries.map((e) => e.title)).toEqual(["confirmed", "consentDenied"]);
    expect(a.openPreviews.value).toEqual([{ previewId: "p1", groupHash: "hash-p1" }]);
    a.markCancelled("p1");
    expect(a.openPreviews.value).toEqual([]);
  });
});

describe("dialog accessibility", () => {
  it("role=dialog, aria-modal, focus trap, Esc goes back from the confirm step, then closes, focus returns to the pill", async () => {
    const b = backend(authed("analyze"));
    const w = await openPanel(b.svc);
    (pill(w).element as HTMLElement).focus();
    await pill(w).trigger("click");
    await flushPromises();
    const dlg = w.get(".bc-dialog");
    expect(dlg.attributes("role")).toBe("dialog");
    expect(dlg.attributes("aria-modal")).toBe("true");
    expect(dlg.element.contains(document.activeElement)).toBe(true);

    // Tab from the last control wraps to the first.
    const focusables = [...dlg.element.querySelectorAll<HTMLElement>("button:not([disabled]), input:not([disabled])")];
    focusables[focusables.length - 1]!.focus();
    await w.get(".bc-scrim").trigger("keydown", { key: "Tab" });
    expect(document.activeElement).toBe(focusables[0]);

    await w.get("[data-testid=mode-prepare]").setValue(true);
    await w.get("[data-testid=perm-continue]").trigger("click");
    const esc = async () => {
      (document.activeElement as HTMLElement).dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
      await flushPromises();
      await nextTick();
    };
    await esc();
    expect(w.get("[data-testid=permissions-dialog]").attributes("data-step")).toBe("edit");
    await esc();
    expect(w.find(".bc-dialog").exists()).toBe(false);
    expect(pill(w).attributes("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(pill(w).element);
    expect(b.calls).toEqual([]);
    w.unmount();
  });
});

describe("AgentClient", () => {
  it("PUTs /session/agent with the CSRF headers and posts cancel with the group hash", async () => {
    const fetch = vi.fn(async (url: string, init: RequestInit) => {
      if (url.endsWith("/session/agent")) return new Response(JSON.stringify({ agentMode: "analyze", paused: true }), { status: 200 });
      return new Response(null, { status: url.includes("/p1/") ? 204 : 404 });
    });
    const c = new AgentClient({ backendUrl: "https://chat.biatec.io", clientKey: "k", fetch: fetch as unknown as typeof globalThis.fetch });
    expect(await c.setAgent("analyze", true)).toEqual({ agentMode: "analyze", paused: true });
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe("https://chat.biatec.io/session/agent");
    expect(init.method).toBe("PUT");
    expect(init.credentials).toBe("include");
    expect(init.headers).toMatchObject({ "X-Biatec-Chat": "1", "X-Biatec-Client-Key": "k" });
    expect(JSON.parse(init.body as string)).toEqual({ mode: "analyze", paused: true });
    expect(await c.cancelPreview("p1", "h")).toBe(true);
    expect(fetch.mock.calls[1]![0]).toBe("https://chat.biatec.io/chat/previews/p1/cancel");
    expect(JSON.parse(fetch.mock.calls[1]![1].body as string)).toEqual({ groupHash: "h" });
    expect(await c.cancelPreview("p2", "h")).toBe(false);
  });

  it("a refused change throws with the backend's code", async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ error: "anonymous_always_analyze" }), { status: 403 }));
    const c = new AgentClient({ backendUrl: "https://x", clientKey: "k", fetch: fetch as unknown as typeof globalThis.fetch });
    await expect(c.setAgent("prepare", false)).rejects.toMatchObject({ status: 403, code: "anonymous_always_analyze" });
  });
});
