import { describe, it, expect, afterEach } from "vitest";
import { mount } from "@vue/test-utils";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ActionPreview, ActionPreviewStatus, ConsentStatus } from "@biatec/chat-protocol";
import { ActionPreviewCard, ReceiptCard, remainingSeconds, formatClock, statusKey, downside } from "../src/components/cards";
import { t } from "../src/i18n";

const NOW = Date.parse("2026-10-20T10:00:00Z");
const iso = (secondsFromNow: number) => new Date(NOW + secondsFromNow * 1000).toISOString();

function fixture(over: Partial<ActionPreview> = {}): ActionPreview {
  return {
    previewId: "p1",
    capability: "swap",
    intent: "Swap 100 USDC for ALGO",
    network: "algorand-testnet",
    genesisId: "testnet-v1.0",
    status: "AwaitingConfirmation",
    expectsAuthenticatorApproval: true,
    approvalReason: "Above your auto-approve limit",
    limit: { bucket: "Daily", remaining: "900", currency: "USD" },
    expiresAt: iso(1800),
    steps: [
      {
        stepId: "s1",
        index: 0,
        title: "Swap on pool",
        status: "AwaitingConfirmation",
        groupHash: "abc",
        from: "ADDR_FROM",
        to: "ADDR_TO",
        assets: [{ assetId: "31566704", symbol: "USDC", amount: "100", decimals: 6, fiatValue: "100", fiatCurrency: "USD" }],
        feeAmount: "0.002 ALGO",
        slippagePercent: "0.5",
        simulationSummary: "Receive at least 410 ALGO",
      },
    ],
    ...over,
  };
}

afterEach(() => {
  document.body.innerHTML = "";
});

const card = (props: Record<string, unknown> = {}) =>
  mount(ActionPreviewCard, { props: { preview: fixture(), nowMs: NOW, ...props }, attachTo: document.body });

async function review(w: ReturnType<typeof card>, tick = true) {
  await w.get("[data-test=review]").trigger("click");
  if (tick) await document.body.querySelector<HTMLInputElement>(".bc-check input")!.click();
  await w.vm.$nextTick();
}

describe("ActionPreviewCard fields", () => {
  it("renders every preview field from a fixture", async () => {
    const w = card();
    const text = w.text();
    expect(w.get(".bc-card-title").text()).toBe("Swap 100 USDC for ALGO");
    expect(text).toContain("Testnet");
    expect(text).toContain("100 USDC");
    expect(text).toContain("0.002 ALGO");
    expect(text).toContain("0.5%");
    expect(text).toContain("Daily: 900 USD left");
    expect(text).toContain("Approval on your phone is expected");
    expect(text).toContain("Awaiting your confirmation");
    expect(w.get("[data-test=countdown]").text()).toBe("30:00");
    expect(w.get("[data-test=downside]").text()).toContain("10.00 USD");
    await w.get(".bc-linkbtn").trigger("click");
    expect(w.get("[data-test=details]").text()).toContain("ADDR_FROM");
    expect(w.get("[data-test=details]").text()).toContain("Receive at least 410 ALGO");
  });
  it("labels Mainnet and omits the downside row when no fiat value is known", () => {
    const f = fixture({ network: "algorand-mainnet", genesisId: "mainnet-v1.0" });
    f.steps[0]!.assets![0]!.fiatValue = null;
    const w = card({ preview: f });
    expect(w.text()).toContain("Mainnet");
    expect(w.find("[data-test=downside]").exists()).toBe(false);
  });
  it("lists steps of a multi-step action with the tx ids of completed ones", () => {
    const f = fixture();
    f.steps = [
      { ...f.steps[0]!, status: "Completed", transactionId: "TX1", explorerUrl: "https://explorer/tx/TX1" },
      { ...f.steps[0]!, stepId: "s2", index: 1, title: "Second" },
    ];
    const w = card({ preview: f });
    expect(w.findAll(".bc-step")).toHaveLength(2);
    expect(w.get(".bc-step a").attributes("href")).toBe("https://explorer/tx/TX1");
    expect(w.text()).toContain("TX1");
  });
});

describe("Confirm gating", () => {
  it("reads Confirm <action> · <amount>, is a 48 px lg button with an icon", () => {
    const b = card().get("[data-test=confirm]");
    expect(b.text()).toBe("Confirm swap · 100 USDC");
    expect(b.attributes("data-size")).toBe("lg");
    expect(b.find("svg").exists()).toBe(true);
    expect(readFileSync(resolve(process.cwd(), "src/styles/ui.css"), "utf8")).toMatch(/data-size="lg"\][^}]*size-control-lg|--size-control-lg: 48px|size-control-lg/);
  });
  it("is disabled until the review step was opened and its checkbox ticked", async () => {
    const w = card();
    const confirm = () => w.get("[data-test=confirm]");
    expect(confirm().attributes("disabled")).toBeDefined();
    await review(w, false); // opened, not ticked
    expect(confirm().attributes("disabled")).toBeDefined();
    document.body.querySelector<HTMLInputElement>(".bc-check input")!.click();
    await w.vm.$nextTick();
    expect(confirm().attributes("disabled")).toBeUndefined();
  });
  it("review shows the five answers and the 10 percent downside", async () => {
    const w = card();
    await review(w, false);
    const dlg = document.body.querySelector(".bc-dialog")!.textContent!;
    for (const h of ["What will happen", "Permissions involved", "Exact details", "Financial implications", "How to stop it"]) expect(dlg).toContain(h);
    expect(dlg).toContain("10.00 USD");
  });
  it("is disabled below 600 s remaining, enabled at exactly 600 s", async () => {
    const low = card({ preview: fixture({ expiresAt: iso(599) }) });
    await review(low);
    expect(low.get("[data-test=confirm]").attributes("disabled")).toBeDefined();
    low.unmount();
    document.body.innerHTML = "";
    const ok = card({ preview: fixture({ expiresAt: iso(600) }) });
    await review(ok);
    expect(ok.get("[data-test=confirm]").attributes("disabled")).toBeUndefined();
  });
  it("the session's absolute lifetime can bind the countdown", async () => {
    const w = card({ sessionExpiresAt: iso(300) });
    expect(w.get("[data-test=countdown]").text()).toBe("05:00");
    expect(remainingSeconds(iso(1800), iso(300), NOW)).toBe(300);
    await review(w);
    expect(w.get("[data-test=confirm]").attributes("disabled")).toBeDefined();
    expect(w.get("[data-test=hint]").text()).toContain("Sign in again");
  });
  it("emits confirm once, flips to a solid border and locks while submitting", async () => {
    const w = card();
    expect(w.get("article").attributes("data-border")).toBe("dashed");
    await review(w);
    await w.get("[data-test=confirm]").trigger("click");
    expect(w.emitted("confirm")).toEqual([["p1"]]);
    expect(w.get("article").attributes("data-border")).toBe("solid");
    await w.get("[data-test=confirm]").trigger("click");
    expect(w.emitted("confirm")).toHaveLength(1);
    await w.setProps({ submitting: true });
    expect(w.get("[data-test=confirm]").attributes("aria-busy")).toBe("true");
  });
  it("emits cancel", async () => {
    const w = card();
    await w.findAll("button").find((b) => b.text() === "Cancel")!.trigger("click");
    expect(w.emitted("cancel")).toEqual([["p1"]]);
  });
});

describe("scope, consent and session states", () => {
  it("Authorize signing asks the host to run the elevate popup for the sign scope", async () => {
    const w = card({ preview: fixture({ status: "AwaitingScope" }) });
    expect(w.text()).toContain("Needs signing permission");
    await w.get("[data-test=scope] button").trigger("click");
    expect(w.emitted("authorize")).toEqual([["sign"]]);
    expect(w.find("[data-test=confirm]").exists()).toBe(false);
  });
  it("shows the waiting-for-phone state with binding code, countdown and Cancel", async () => {
    const consent: ConsentStatus = { previewId: "p1", stepId: "s1", state: "Pending", bindingCode: "4821", expiresAt: iso(240) };
    const w = card({ preview: fixture({ status: "AwaitingApproval" }), consent });
    expect(w.text()).toContain("Waiting for approval on your phone");
    expect(w.get("[data-test=binding-code]").text()).toBe("4821");
    expect(w.get("[data-test=consent-countdown]").text()).toBe("04:00");
    await w.get("[data-test=consent] button").trigger("click");
    expect(w.emitted("cancel")).toEqual([["p1"]]);
  });
  it("warns under 20 minutes of session and offers finish or save", async () => {
    const w = card({ sessionExpiresAt: iso(1100) });
    const warn = w.get("[data-test=session-warning]");
    expect(warn.text()).toContain("18:20");
    const [finish, save] = warn.findAll("button");
    await finish!.trigger("click");
    await save!.trigger("click");
    expect(w.emitted("finish")).toEqual([["p1"]]);
    expect(w.emitted("save")).toEqual([["p1"]]);
    expect(card({ sessionExpiresAt: iso(1300) }).find("[data-test=session-warning]").exists()).toBe(false);
  });
});

describe("statuses and labels", () => {
  const cases: [ActionPreviewStatus, ConsentStatus["state"] | null, string][] = [
    ["AwaitingConfirmation", null, "Awaiting your confirmation"],
    ["AwaitingScope", null, "Needs signing permission"],
    ["AwaitingApproval", null, "Waiting for approval on your phone"],
    ["AwaitingApproval", "Pending", "Waiting for approval on your phone"],
    ["Signing", "Approved", "Approved · signing"],
    ["Submitted", null, "Submitted · waiting for confirmation"],
    ["Completed", null, "Completed"],
    ["AwaitingApproval", "Denied", "Rejected on your phone"],
    ["Failed", null, "Failed"],
    ["AwaitingApproval", "TimedOut", "Approval timed out · nothing was signed"],
    ["Cancelled", null, "Cancelled"],
    ["Expired", null, "Expired"],
  ];
  for (const [status, state, label] of cases) {
    it(`${status}${state ? ` + consent ${state}` : ""} shows "${label}"`, () => {
      const consent = state ? ({ previewId: "p1", stepId: "s1", state } as ConsentStatus) : null;
      const w = card({ preview: fixture({ status }), consent });
      expect(w.get(".bc-badge-label").text()).toBe(label);
      expect(t(`status.${statusKey(status, state)}`)).toBe(label);
      const dashed = w.get("article").attributes("data-border") === "dashed";
      expect(dashed).toBe(status === "AwaitingConfirmation" || status === "AwaitingScope");
    });
  }
});

describe("countdown style", () => {
  it("uses tabular numerals and a neutral colour: no red, no animation", () => {
    const css = readFileSync(resolve(process.cwd(), "src/styles/cards.css"), "utf8");
    const rule = css.match(/\.bc-countdown\s*\{[^}]*\}/)![0];
    expect(rule).toContain("tabular-nums");
    expect(rule).toContain("--color-text-secondary");
    expect(rule).not.toMatch(/danger|red|animation/);
    expect(css).not.toMatch(/@keyframes|animation:/);
    expect(formatClock(65)).toBe("01:05");
  });
});

describe("ReceiptCard", () => {
  it("shows the tx id with an explorer link and an evidence download", () => {
    const f = fixture({ status: "Completed" });
    f.steps[0] = { ...f.steps[0]!, status: "Completed", transactionId: "TXID123", explorerUrl: "https://explorer.example/tx/TXID123" };
    const w = mount(ReceiptCard, { props: { preview: f, evidenceUrl: "https://chat.example/evidence/p1" } });
    expect(w.get("[data-test=tx-id]").text()).toBe("TXID123");
    expect(w.get("[data-test=tx-link]").attributes("href")).toBe("https://explorer.example/tx/TXID123");
    expect(w.get("[data-test=evidence-link]").attributes("href")).toBe("https://chat.example/evidence/p1");
    expect(w.get("[data-test=evidence-link]").text()).toBe("Download evidence");
    expect(w.get("article").attributes("data-border")).toBe("solid");
    expect(w.text()).toContain("Completed");
  });
  it("never renders a javascript: link", () => {
    const f = fixture({ status: "Completed" });
    f.steps[0] = { ...f.steps[0]!, transactionId: "T", explorerUrl: "javascript:alert(1)" };
    const w = mount(ReceiptCard, { props: { preview: f, evidenceUrl: "javascript:alert(1)" } });
    expect(w.find("a").exists()).toBe(false);
  });
});

describe("model", () => {
  it("downside needs a fiat value", () => {
    expect(downside(fixture())).toEqual({ amount: "10.00", currency: "USD" });
    const f = fixture();
    f.steps[0]!.assets![0]!.fiatValue = null;
    expect(downside(f)).toBeNull();
  });
});
