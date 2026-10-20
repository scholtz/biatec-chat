import { describe, it, expect, afterEach, vi } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import type { ChatEvent, SafeCard as SafeCardData, SessionState } from "@biatec/chat-protocol";
import BiatecChatRoot from "../src/components/BiatecChatRoot.vue";
import { SafeCard } from "../src/components/cards";
import { isChatEvent } from "../src/transport/sse";
import { resolveOptions } from "../src/options";
import type { ChatServices } from "../src/components/chat/useChat";

const SAFE_ADDR = "SAFEADDRESS7777777777777777777777777777777777777777777777";
const BOB = "BOBADDRESS77777777777777777777777777777777777777777777777";

function listCard(over: Partial<SafeCardData> = {}): SafeCardData {
  return {
    source: "List",
    network: "algorand-testnet",
    safes: [
      {
        appId: "778",
        address: SAFE_ADDR,
        name: "Team Safe",
        network: "algorand-testnet",
        paused: false,
        yourAddresses: [BOB],
        signerGroups: [
          { groupId: "1", name: "Signers", threshold: 2, memberCount: 3, membershipEpoch: 4, allowedActions: ["payment"], isAdminGroup: true },
        ],
        pendingProposals: [
          {
            proposalId: "2", groupId: "1", kind: "transactionGroup", status: "ReadyToExecute", approvals: 2, threshold: 2,
            approvedByYou: true, proposer: BOB, payloadVersion: "3", expiryRound: "9999999",
          },
          {
            proposalId: "1", groupId: "1", kind: "membershipChange", status: "AwaitingApprovals", approvals: 1, threshold: 2,
            approvedByYou: false, proposer: BOB, payloadVersion: "5", expiryRound: "12345678",
          },
        ],
        proposalsTruncated: false,
        proposalsCouldNotBeRead: false,
      },
    ],
    couldNotRead: [],
    ...over,
  };
}

const createCard: SafeCardData = {
  source: "Create",
  network: "algorand-testnet",
  safes: [{ appId: "778", address: SAFE_ADDR, name: "Team Safe", network: "algorand-testnet", yourAddresses: [BOB], creation: "NeedsFunding" }],
};

afterEach(() => {
  document.body.innerHTML = "";
});

describe("SafeCard", () => {
  it("safe.list: shows each safe, its groups with epoch, and each pending proposal's approvals, payload version and expiry", () => {
    const w = mount(SafeCard, { props: { card: listCard() } });
    expect(w.get("[data-test=safe-name]").text()).toBe("Team Safe");
    expect(w.get("[data-test=safe-app-id]").text()).toBe("778");
    expect(w.get("[data-test=safe-address]").text()).toBe(SAFE_ADDR);
    expect(w.text()).toContain("Testnet");
    expect(w.get("[data-test=safe-status]").text()).toBe("Active");

    const group = w.get("[data-test=safe-group]");
    expect(group.text()).toContain("Signers · 2 of 3");
    expect(group.get("[data-test=safe-epoch]").text()).toBe("Membership epoch 4");

    const proposals = w.findAll("[data-test=safe-proposal]");
    expect(proposals).toHaveLength(2);
    expect(proposals[0]!.get("[data-test=proposal-status]").text()).toBe("Ready to execute");
    expect(proposals[0]!.get("[data-test=proposal-you]").text()).toBe("You approved");
    expect(proposals[1]!.text()).toContain("Proposal 1 · membershipChange");
    expect(proposals[1]!.get("[data-test=proposal-status]").text()).toBe("Awaiting approvals");
    expect(proposals[1]!.get("[data-test=proposal-progress]").text()).toBe("1 of 2 approvals");
    expect(proposals[1]!.get("[data-test=proposal-version]").text()).toBe("5");
    expect(proposals[1]!.get("[data-test=proposal-expiry]").text()).toBe("12345678");
    const bar = proposals[1]!.get("[role=progressbar]");
    expect(bar.attributes("aria-valuenow")).toBe("1");
    expect(bar.attributes("aria-valuemax")).toBe("2");
    expect(bar.get(".bc-progress-fill").attributes("style")).toContain("width: 50%");
  });

  it("safe.list: says when proposals could not be read and which safes could not be read", () => {
    const base = listCard().safes[0]!;
    const w = mount(SafeCard, {
      props: { card: listCard({ safes: [{ ...base, pendingProposals: [], proposalsCouldNotBeRead: true }], couldNotRead: ["555", "666"] }) },
    });
    expect(w.find("[data-test=safe-proposal]").exists()).toBe(false);
    expect(w.find("[data-test=safe-proposals-unreadable]").exists()).toBe(true);
    expect(w.get("[data-test=safe-could-not-read]").text()).toContain("555, 666");
  });

  it("safe.list: a safe whose proposals were not loaded says so instead of 'No pending proposals'", () => {
    const base = listCard().safes[0]!;
    const w = mount(SafeCard, { props: { card: listCard({ safes: [{ ...base, pendingProposals: [], proposalsNotLoaded: true }] }) } });
    expect(w.find("[data-test=safe-proposals-not-loaded]").exists()).toBe(true);
    expect(w.text()).not.toContain("No pending proposals");
  });

  it("safe.create: shows the app id and safe address once deployed, without a proposal queue", () => {
    const w = mount(SafeCard, { props: { card: createCard } });
    expect(w.get(".bc-card-title").text()).toBe("Your new safe");
    expect(w.get("[data-test=safe-app-id]").text()).toBe("778");
    expect(w.get("[data-test=safe-address]").text()).toBe(SAFE_ADDR);
    expect(w.get("[data-test=safe-status]").text()).toBe("Deployed · needs ALGO for storage");
    expect(w.text()).not.toContain("Pending proposals");
  });

  it("renders safe names and proposal kinds from the chain as text, never HTML", () => {
    const base = listCard().safes[0]!;
    const evil = '<img src=x onerror="alert(1)">';
    const w = mount(SafeCard, {
      props: { card: listCard({ safes: [{ ...base, name: evil, pendingProposals: [{ ...base.pendingProposals![0]!, kind: "<b>x</b>" }] }] }) },
    });
    expect(w.find("img").exists()).toBe(false);
    expect(w.find("b").exists()).toBe(false);
    expect(w.get("[data-test=safe-name]").text()).toBe(evil);
    expect(w.get("[data-test=safe-proposal]").text()).toContain("<b>x</b>");
  });
});

describe("safe_card in the conversation", () => {
  const authed: SessionState = { sessionId: "s", tier: "Authenticated", scopes: ["openid"], expiresAt: "2030-01-01T00:00:00Z" };

  function services(events: ChatEvent[]): ChatServices {
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
        session: vi.fn(async () => authed),
        startLogin: vi.fn(async () => null),
        startElevate: vi.fn(async () => null),
        status: vi.fn(async () => "Pending" as const),
        logoutUrl: vi.fn(() => "https://chat.biatec.io/auth/logout"),
      },
      pollMs: 1,
      timeoutMs: 50,
    };
  }

  async function ask(events: ChatEvent[], svc: ChatServices = services(events)) {
    const w = mount(BiatecChatRoot, {
      props: { options: resolveOptions({ backendUrl: "https://chat.biatec.io", clientKey: "k" }), services: svc },
      attachTo: document.body,
    });
    await w.get(".bc-launcher").trigger("click");
    await flushPromises();
    await w.get("textarea").setValue("show my safes");
    await w.get("form").trigger("submit");
    await flushPromises();
    return w;
  }

  it("renders the card from the safe_card event in the assistant message", async () => {
    const w = await ask([
      { type: "tool_status", toolCallId: "t1", label: "Reading your safes", state: "Started" },
      { type: "safe_card", card: listCard() },
      { type: "tool_status", toolCallId: "t1", label: "Reading your safes", state: "Succeeded" },
      { type: "text_delta", text: "You have one safe." },
    ]);
    const card = w.get("[data-role=assistant] [data-test=safe-card]");
    expect(card.get("[data-test=safe-app-id]").text()).toBe("778");
    expect(card.findAll("[data-test=safe-proposal]")).toHaveLength(2);
    w.unmount();
  });

  it("never builds a card from model text, even text that looks like safe data", async () => {
    const text = JSON.stringify({ type: "safe_card", card: listCard() });
    const w = await ask([{ type: "text_delta", text: `Your safe: app id 999, ${text}` }]);
    expect(w.find("[data-test=safe-card]").exists()).toBe(false);
    expect(w.get("[data-role=assistant] .bc-para").text()).toContain("app id 999");
    w.unmount();
  });

  it("keeps the thinking indicator after a safe card until text arrives", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const svc = services([]);
    svc.transport.send = vi.fn(async function* () {
      yield { type: "safe_card", card: listCard() } as ChatEvent;
      await gate;
      yield { type: "text_delta", text: "Done." } as ChatEvent;
    });
    const w = await ask([], svc);
    expect(w.find("[data-test=safe-card]").exists()).toBe(true);
    expect(w.find(".bc-thinking").exists()).toBe(true);
    release();
    await flushPromises();
    expect(w.find(".bc-thinking").exists()).toBe(false);
    w.unmount();
  });

  it("announces a reply that is only a safe card", async () => {
    const w = await ask([{ type: "safe_card", card: listCard() }]);
    await flushPromises();
    expect(w.get("[data-testid=announcer]").text()).toBe("Safe card: 1 safe(s), 2 pending proposal(s).");
    w.unmount();
  });

  it("the transport accepts safe_card frames and drops malformed ones", () => {
    expect(isChatEvent({ type: "safe_card", card: listCard() })).toBe(true);
    expect(isChatEvent({ type: "safe_card", card: createCard })).toBe(true);
    expect(isChatEvent({ type: "safe_card" })).toBe(false);
    expect(isChatEvent({ type: "safe_card", card: { network: "algorand-testnet", safes: "nope" } })).toBe(false);
    const base = listCard();
    const safe = base.safes[0]!;
    const bad: unknown[] = [
      { ...base, safes: [null] },
      { ...base, safes: [{ ...safe, appId: 778 }] },
      { ...base, safes: [{ ...safe, name: undefined }] },
      { ...base, safes: [{ ...safe, signerGroups: [{ name: "x" }] }] },
      { ...base, safes: [{ ...safe, pendingProposals: [{ ...safe.pendingProposals![0]!, expiryRound: 5 }] }] },
      { ...base, safes: [{ ...safe, yourAddresses: [1] }] },
      { ...base, couldNotRead: "555" },
      { ...base, couldNotRead: [555] },
    ];
    for (const card of bad) expect(isChatEvent({ type: "safe_card", card })).toBe(false);
  });
});
