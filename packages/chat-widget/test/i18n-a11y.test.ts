import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import axe from "axe-core";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ActionPreview, AgentMode, ChatEvent, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { resolveOptions } from "../src/options";
import type { WidgetServices } from "../src/api/core";
import { resetHostApi } from "../src/api";
import { en, sk, i18n, setLocale, resolvedLocale, t } from "../src/i18n";

const read = (p: string) => readFileSync(resolve(process.cwd(), p), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

type Tree = { [k: string]: string | Tree };
const flat = (o: Tree, prefix = ""): Record<string, string> =>
  Object.entries(o).reduce<Record<string, string>>((acc, [k, v]) => {
    if (typeof v === "string") acc[prefix + k] = v;
    else Object.assign(acc, flat(v, `${prefix}${k}.`));
    return acc;
  }, {});
const placeholders = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]!).sort();

// jsdom has no canvas; axe probes it for icon ligatures. A null context makes axe skip that probe quietly.
HTMLCanvasElement.prototype.getContext = (() => null) as never;

afterEach(() => {
  document.body.innerHTML = "";
  resetHostApi();
  setLocale("en");
  vi.unstubAllGlobals();
});

describe("i18n catalogs", () => {
  const enFlat = flat(en as Tree);
  const skFlat = flat(sk as Tree);

  it("Slovak has exactly the English keys", () => {
    expect(Object.keys(skFlat).sort()).toEqual(Object.keys(enFlat).sort());
    expect(Object.keys(enFlat).length).toBeGreaterThan(150);
  });

  it("every Slovak message is filled in and keeps the English placeholders", () => {
    for (const [key, value] of Object.entries(skFlat)) {
      expect(value.trim(), key).not.toBe("");
      expect(placeholders(value), key).toEqual(placeholders(enFlat[key]!));
    }
  });

  it("Slovak actually translates the UI (most messages differ from English)", () => {
    const same = Object.keys(enFlat).filter((k) => enFlat[k] === skFlat[k]);
    expect(same.length / Object.keys(enFlat).length).toBeLessThan(0.1);
  });

  it("the catalog files on disk are all registered (no catalog is left out of an index)", () => {
    for (const file of ["ui", "cards", "chat", "agent", "activity", "safe", "export"]) {
      expect(Object.keys(JSON.parse(read(`src/i18n/sk/${file}.json`))), file).toEqual(Object.keys(JSON.parse(read(`src/i18n/en/${file}.json`))));
    }
  });

  it("every t('…') key used in the sources exists in English and Slovak", () => {
    const keys = new Set<string>();
    const files = [
      "src/components/BiatecChatRoot.vue",
      "src/components/ChatLauncher.vue",
      "src/components/ChatPanel.vue",
      "src/components/activity/ActivityTimeline.vue",
      "src/components/agent/AgentNotices.vue",
      "src/components/export/ExportView.vue",
      "src/components/agent/PermissionsDialog.vue",
      "src/components/agent/useAgent.ts",
      "src/components/cards/ActionPreviewCard.vue",
      "src/components/cards/ReceiptCard.vue",
      "src/components/cards/ReviewDialog.vue",
      "src/components/cards/SafeCard.vue",
      "src/components/chat/ChatView.vue",
      "src/components/chat/useChat.ts",
      "src/components/chat/auth.ts",
      "src/components/chat/suggestions.ts",
      "src/components/shell/BiatecChatInline.vue",
      "src/components/shell/MinimizedBar.vue",
      "src/components/shell/ShellHeader.vue",
      "src/components/shell/StatusBar.vue",
      "src/ui/Dialog.vue",
      "src/ui/Spinner.vue",
      "src/ui/Toast.vue",
      "src/ui/StatusBadge.vue",
    ];
    for (const f of files) for (const m of read(f).matchAll(/\bt\(\s*["']([\w.]+)["']/g)) keys.add(m[1]!);
    expect(keys.size).toBeGreaterThan(80);
    for (const k of keys) {
      expect(enFlat[k], `en: ${k}`).toBeDefined();
      expect(skFlat[k], `sk: ${k}`).toBeDefined();
    }
  });

  it("t() follows setLocale: sk, sk-SK and en-GB", () => {
    setLocale("sk");
    expect(t("chat.send")).toBe("Odoslať správu");
    setLocale("en");
    expect(t("chat.send")).toBe("Send message");
    setLocale("sk-SK");
    expect(t("chat.send")).toBe("Odoslať správu");
    setLocale("en-GB");
    expect(t("chat.send")).toBe("Send message");
  });

  it("an unsupported locale (de) falls back to English", () => {
    setLocale("de");
    expect(resolvedLocale("de")).toBe("en");
    expect(resolvedLocale("de-AT")).toBe("en");
    expect(resolvedLocale("sk-SK")).toBe("sk");
    expect(t("chat.send")).toBe("Send message");
    expect(t("shell.launcher")).toBe("Ask Biatec AI");
    expect(i18n.global.availableLocales).toEqual(expect.arrayContaining(["en", "sk"]));
  });
});

const opts = (extra: object = {}) => resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k", ...extra });
const authed = (agentMode: AgentMode = "prepare"): SessionState => ({
  sessionId: "s",
  tier: "Authenticated",
  email: "alice@example.com",
  primaryAddress: "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
  scopes: ["openid", "sign"],
  expiresAt: "2030-01-01T00:00:00Z",
  agentMode,
  paused: false,
});
const preview = (): ActionPreview =>
  ({
    previewId: "p1",
    capability: "dex.swap",
    intent: "Swap 100 USDC for ALGO",
    network: "algorand-testnet",
    genesisId: "testnet-v1.0",
    status: "AwaitingConfirmation",
    expectsAuthenticatorApproval: true,
    limit: { bucket: "Daily", remaining: "900", currency: "USD" },
    expiresAt: new Date(Date.now() + 1800_000).toISOString(),
    steps: [
      {
        stepId: "s1",
        index: 0,
        title: "Swap on pool",
        status: "AwaitingConfirmation",
        groupHash: "HASH",
        assets: [{ assetId: "31566704", symbol: "USDC", amount: "100", decimals: 6 }],
        feeAmount: "0.002 ALGO",
      },
    ],
  }) as ActionPreview;

function services(session: SessionState, events: ChatEvent[] = []): WidgetServices {
  return {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {
        for (const e of events) yield e;
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
      setAgent: vi.fn(async (mode: AgentMode, paused: boolean) => ({ agentMode: mode, paused })),
      cancelPreview: vi.fn(async () => true),
    },
    // The export view (T-094) in the Activity sheet: one conversation with evidence.
    retention: {
      list: vi.fn(async () => [
        { conversationId: "c1", createdAt: "2026-10-19T08:30:00Z", lastMessageAt: "2026-10-19T09:00:00Z", current: true, evidenceEntries: 2 },
      ]),
      evidence: vi.fn(async () => ({ name: "b.json", text: "{}" })),
      erase: vi.fn(async () => ({ status: "erased", conversations: 1, payloadsDeleted: 1, conversationId: "c2" })),
    },
    pollMs: 1,
    timeoutMs: 50,
  } as WidgetServices;
}

async function open(w: VueWrapper) {
  await w.get(".bc-launcher").trigger("click");
  await flushPromises();
}
async function ask(w: VueWrapper, text = "swap") {
  await w.get("textarea").setValue(text);
  await w.get("[data-testid=send]").trigger("click");
  await flushPromises();
}

/** axe over the whole document (the widget and its dialogs). Colour contrast is checked on the tokens below, because
 *  jsdom has no layout or canvas for axe's pixel-based check. */
async function violations() {
  const result = await axe.run(document.body, {
    runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"] },
    rules: { "color-contrast": { enabled: false }, region: { enabled: false }, "landmark-one-main": { enabled: false }, "page-has-heading-one": { enabled: false } },
  });
  return result.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}

type View = { name: string; run: (w: VueWrapper) => Promise<void>; session?: SessionState; events?: ChatEvent[] };
const views: View[] = [
  { name: "empty", run: async () => {} },
  {
    name: "conversation",
    events: [{ type: "text_delta", text: "Algorand is a blockchain." }],
    run: async (w) => ask(w, "What is the blockchain?"),
  },
  {
    name: "preview",
    session: authed(),
    events: [{ type: "preview_card", preview: preview() }],
    run: async (w) => ask(w),
  },
  {
    name: "review dialog",
    session: authed(),
    events: [{ type: "preview_card", preview: preview() }],
    run: async (w) => {
      await ask(w);
      await w.get("[data-test=review]").trigger("click");
      await flushPromises();
    },
  },
  {
    name: "permissions dialog",
    session: authed("analyze"),
    run: async (w) => {
      await w.get("[data-testid=status-pill]").trigger("click");
      await flushPromises();
    },
  },
  {
    name: "permissions confirm step",
    session: authed("analyze"),
    run: async (w) => {
      await w.get("[data-testid=status-pill]").trigger("click");
      await w.get("[data-testid=mode-prepare]").setValue(true);
      await flushPromises();
      await w.get("[data-testid=perm-continue]").trigger("click");
      await flushPromises();
    },
  },
  {
    name: "activity",
    session: authed(),
    events: [{ type: "preview_card", preview: preview() }],
    run: async (w) => {
      await ask(w);
      await w.get("[data-testid=activity-toggle]").trigger("click");
      await flushPromises();
    },
  },
  {
    name: "erase confirm step",
    session: authed(),
    run: async (w) => {
      await w.get("[data-testid=activity-toggle]").trigger("click");
      await flushPromises();
      await w.get("[data-testid=export-erase]").trigger("click");
      await flushPromises();
    },
  },
];

describe.each(["dark", "light"] as const)("axe, data-theme=%s", (theme) => {
  describe.each(["en", "sk"])("locale %s", (locale) => {
    for (const v of views) {
      it(`${v.name}: no violations`, async () => {
        const w = mount(BiatecChatRoot, {
          props: { options: opts({ theme, locale }), services: services(v.session ?? { sessionId: "s", tier: "Anonymous", scopes: [], expiresAt: "2030-01-01T00:00:00Z" }, v.events) },
          attachTo: document.body,
        });
        await open(w);
        await v.run(w);
        const root = document.body.querySelector(".biatec-chat")!;
        expect(root.getAttribute("data-theme")).toBe(theme);
        expect(root.closest("[lang]")?.getAttribute("lang") ?? root.getAttribute("lang")).toBe(locale);
        expect(await violations()).toEqual([]);
        w.unmount();
      });
    }
  });
});

// Colour contrast from the design tokens: WCAG AA is 4.5:1 for text, 3:1 for UI components and focus indicators.
function parseColor(v: string): [number, number, number, number] {
  const hex = /^#([0-9a-f]{6})$/i.exec(v);
  if (hex) {
    const n = parseInt(hex[1]!, 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255, 1];
  }
  const m = /^rgba?\(([^)]+)\)$/.exec(v)!;
  const p = m[1]!.split(",").map((x) => Number(x.trim()));
  return [p[0]!, p[1]!, p[2]!, p[3] ?? 1];
}
const over = (fg: ReturnType<typeof parseColor>, bg: ReturnType<typeof parseColor>): ReturnType<typeof parseColor> => [
  fg[0] * fg[3] + bg[0] * (1 - fg[3]),
  fg[1] * fg[3] + bg[1] * (1 - fg[3]),
  fg[2] * fg[3] + bg[2] * (1 - fg[3]),
  1,
];
const lum = ([r, g, b]: number[]) => {
  const c = [r!, g!, b!].map((x) => {
    const s = x / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!;
};
const ratio = (fg: string, bg: string, under = bg) => {
  const base = parseColor(under);
  const b = over(parseColor(bg), base);
  const f = over(parseColor(fg), b);
  const [hi, lo] = [lum(f), lum(b)].sort((a, c) => c - a);
  return (hi! + 0.05) / (lo! + 0.05);
};

function tokens(theme: "dark" | "light"): Record<string, string> {
  const css = read("src/styles/tokens.css");
  const block = (head: RegExp) => {
    const m = head.exec(css)!;
    return css.slice(m.index + m[0].length, css.indexOf("}", m.index));
  };
  const decl = (b: string) => Object.fromEntries([...b.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((x) => [x[1]!, x[2]!.trim()]));
  const dark = decl(block(/:host,\s*\.biatec-chat\[data-standalone\]\s*\{/));
  return theme === "dark" ? dark : { ...dark, ...decl(block(/:host\(\[data-theme="light"\]\),\s*\.biatec-chat\[data-standalone\]\[data-theme="light"\]\s*\{/)) };
}

describe.each(["dark", "light"] as const)("contrast, %s tokens", (theme) => {
  const tk = tokens(theme);
  const surfaces = ["--color-bg-primary", "--color-bg-secondary", "--color-bg-elevated", "--color-bg-interactive"];

  it("body and secondary text reach 4.5:1 on every surface", () => {
    for (const s of surfaces)
      for (const fg of ["--color-text-primary", "--color-text-secondary"]) expect(ratio(tk[fg]!, tk[s]!, tk["--color-bg-primary"]!), `${fg} on ${s}`).toBeGreaterThanOrEqual(4.5);
  });

  it("brand text, links and status colours reach 4.5:1 on the surfaces they are drawn on", () => {
    for (const s of ["--color-bg-primary", "--color-bg-secondary", "--color-bg-elevated"]) {
      for (const fg of ["--color-brand-text", "--color-status-success", "--color-status-warning", "--color-status-danger", "--color-status-info"])
        expect(ratio(tk[fg]!, tk[s]!, tk["--color-bg-primary"]!), `${fg} on ${s}`).toBeGreaterThanOrEqual(4.5);
    }
  });

  it("text on the primary button and on subtle status backgrounds reaches 4.5:1", () => {
    expect(ratio(tk["--color-brand-on"]!, tk["--color-brand-primary"]!)).toBeGreaterThanOrEqual(4.5);
    expect(ratio(tk["--color-brand-on"]!, tk["--color-brand-hover"]!)).toBeGreaterThanOrEqual(4.5);
    expect(ratio(tk["--color-brand-on"]!, tk["--color-brand-pressed"]!)).toBeGreaterThanOrEqual(4.5);
    for (const tone of ["success", "warning", "danger", "info"])
      expect(ratio(tk[`--color-status-${tone}`]!, tk[`--color-status-${tone}-subtle`]!, tk["--color-bg-secondary"]!), tone).toBeGreaterThanOrEqual(4.5);
    expect(ratio(tk["--color-brand-text"]!, tk["--color-brand-subtle"]!, tk["--color-bg-secondary"]!)).toBeGreaterThanOrEqual(4.5);
  });

  it("the focus ring reaches 3:1 on every surface", () => {
    for (const s of surfaces) expect(ratio(tk["--color-border-focus"]!, tk[s]!, tk["--color-bg-primary"]!), s).toBeGreaterThanOrEqual(3);
  });
});

describe("touch targets and reduced motion (CSS)", () => {
  const css = read("src/styles/a11y.css");
  const coarse = css.slice(css.indexOf("@media (pointer: coarse)"), css.indexOf("@media (prefers-reduced-motion"));

  it("coarse pointers get 44 px controls for buttons, chips, actions, the status pill and the send button", () => {
    expect(read("src/styles/tokens.css")).toContain("--size-icon-hit-touch: 44px");
    for (const s of [".bc-btn", ".bc-chip", ".bc-action", ".bc-status-pill", ".bc-send", ".bc-check", ".bc-linkbtn"]) expect(coarse, s).toContain(s);
    expect(coarse).toContain("var(--size-icon-hit-touch)");
    expect(read("src/styles/ui.css")).toMatch(/@media \(pointer: coarse\)\s*\{[^}]*\.bc-iconbtn[^}]*--size-icon-hit-touch/);
  });

  it("a11y.css is imported last and every selector is scoped to the widget root", () => {
    const entry = read("src/styles.css");
    expect(entry.lastIndexOf("@import")).toBe(entry.indexOf('@import "./styles/a11y.css"'));
    for (const m of css.matchAll(/(?:^|[};])\s*([^{}@;]+)\{/g)) for (const s of m[1]!.split(",")) expect(s.trim()).toMatch(/^\.biatec-chat/);
    expect(css).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });

  it("reduced motion switches every animation and transition off", () => {
    const rm = css.slice(css.indexOf("@media (prefers-reduced-motion"));
    expect(rm).toMatch(/\.biatec-chat \*[\s\S]*animation: none !important/);
    expect(rm).toMatch(/transition: none !important/);
  });

  it("every keyframe animation in the widget sheets is used only where reduced motion can reach it (all under .biatec-chat)", () => {
    for (const f of ["src/styles/ui.css", "src/components/chat/chat.css", "src/components/shell/shell.css"]) {
      const sheet = read(f);
      for (const m of sheet.matchAll(/(?:^|[};])\s*([^{}@;]+)\{[^}]*\banimation:/g)) for (const s of m[1]!.split(",")) expect(s.trim(), f).toMatch(/^\.biatec-chat/);
    }
  });
});
