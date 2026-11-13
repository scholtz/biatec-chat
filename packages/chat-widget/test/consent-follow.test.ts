import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { mount, type VueWrapper } from "@vue/test-utils";
import type { ActionPreview, ActionPreviewStatus, ChatEvent, ConsentStatus } from "@biatec/chat-protocol";
import ActionPreviewCard from "../src/components/cards/ActionPreviewCard.vue";
import { ConsentClient, type ConsentApi, type ConsentPoll, type ConsentStreamEvent } from "../src/components/chat/consent";
import { toConfirmResult, type ConfirmResult } from "../src/components/chat/previews";
import { useChat, type ChatServices } from "../src/components/chat/useChat";
import { SseParser, TransportError } from "../src/transport";

// ---- fixtures -------------------------------------------------------------------------------------------------------

const CODE = "482913";
const T0 = Date.parse("2030-01-01T12:00:00Z");
const iso = (ms: number) => new Date(ms).toISOString();

function preview(status: ActionPreviewStatus = "AwaitingConfirmation", over: Partial<ActionPreview> = {}): ActionPreview {
  return {
    previewId: "p1",
    capability: "swap",
    intent: "Swap 100 USDC for ALGO",
    network: "algorand-testnet",
    genesisId: "testnet-v1.0",
    status,
    expectsAuthenticatorApproval: true,
    expiresAt: iso(Date.now() + 1_800_000),
    steps: [{ stepId: "s1", index: 0, title: "Swap", status, groupHash: "HASH-1", assets: [{ assetId: "1", symbol: "USDC", amount: "100", decimals: 6 }] }],
    ...over,
  };
}
const pending = (over: Partial<ConsentStatus> = {}): ConsentStatus => ({ previewId: "p1", stepId: "s1", state: "Pending", bindingCode: CODE, expiresAt: iso(Date.now() + 300_000), ...over });
const ended = (state: ConsentStatus["state"]): ConsentStatus => ({ previewId: "p1", stepId: "s1", state, bindingCode: CODE });
const result = (status: string, over: Partial<ConfirmResult> = {}): ConfirmResult => ({ status, message: "", txIds: [], consent: null, reauthorizeScope: null, raw: { status }, ...over });
const status = (consent: ConsentStatus): ConsentStreamEvent => ({ kind: "status", consent });
const done = (r: ConfirmResult): ConsentStreamEvent => ({ kind: "result", result: r });

/** Resolves when the signal aborts: a stream that stays open. */
const open = (signal?: AbortSignal) => new Promise<void>((r) => signal?.addEventListener("abort", () => r(), { once: true }));

interface Fake extends ConsentApi {
  poll: ReturnType<typeof vi.fn>;
  stream: ReturnType<typeof vi.fn>;
  prediction: ReturnType<typeof vi.fn>;
}
function fakeConsent(over: Partial<ConsentApi> = {}): Fake {
  return {
    poll: vi.fn(async (): Promise<ConsentPoll> => ({ ok: true, consent: pending(), result: null })),
    stream: vi.fn(async function* (_id: string, signal?: AbortSignal) {
      yield status(pending());
      await open(signal);
    }),
    prediction: vi.fn(async () => null),
    ...over,
  } as Fake;
}

/** The widget's chat composable, mounted the way the widget does, with the preview card delivered through a reply. */
async function mountChat(consent: ConsentApi | undefined, events: ChatEvent[], extra: Partial<ChatServices> = {}) {
  const confirm = vi.fn(async () => result("consent_pending", { consent: pending() }));
  const cancelPreview = vi.fn(async () => true);
  const onEvent = vi.fn();
  const onConfirmed = vi.fn();
  const svc: ChatServices = {
    transport: {
      open: vi.fn(async () => "success" as const),
      retryOnSignIn: vi.fn(async () => true),
      send: vi.fn(async function* () {
        for (const e of events) yield e;
      }),
      dispose: vi.fn(),
    },
    auth: { session: vi.fn(async () => null), startLogin: vi.fn(), startElevate: vi.fn(), status: vi.fn(), logoutUrl: vi.fn(() => "") } as unknown as ChatServices["auth"],
    previews: { confirm },
    cancelPreview,
    consent,
    onEvent,
    onConfirmed,
    ...extra,
  };
  let chat!: ReturnType<typeof useChat>;
  const wrapper: VueWrapper = mount(defineComponent({ setup: () => ((chat = useChat(svc)), () => h("div")) }));
  await chat.send("swap");
  await settle();
  return { chat, wrapper, confirm, cancelPreview, onEvent, onConfirmed, entry: () => chat.messages[1]!.previews![0]! };
}

/** Let promise continuations (and timers due now) run. */
const settle = () => vi.advanceTimersByTimeAsync(0);
const advance = (ms: number) => vi.advanceTimersByTimeAsync(ms);

let wrapper: VueWrapper | null = null;
beforeEach(() => {
  vi.useFakeTimers({ now: T0, toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  vi.spyOn(Math, "random").mockReturnValue(0); // no jitter: backoff steps land exactly
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.useRealTimers();
  vi.restoreAllMocks();
  Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
});

// ---- following a consent ---------------------------------------------------------------------------------------------

describe("following a phone approval", () => {
  it("opens the consent stream once after consent_pending, keeps the code while pending, and shows the submitted result", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(pending());
        await gate;
        yield status(ended("Approved"));
        yield done(result("submitted", { txIds: ["TX1"] }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();

    expect(consent.stream).toHaveBeenCalledTimes(1);
    expect(consent.poll).not.toHaveBeenCalled();
    expect(m.entry().preview.status).toBe("AwaitingApproval");
    expect(m.entry().consent).toMatchObject({ state: "Pending", bindingCode: CODE });

    release();
    await settle();
    expect(m.entry().preview.status).toBe("Submitted");
    expect(m.entry().preview.steps[0]!.transactionId).toBe("TX1");
    expect(m.entry().consent).toMatchObject({ state: "Approved", bindingCode: null });
    expect(consent.stream).toHaveBeenCalledTimes(1);
    // Activity hears about the consent and the settled preview.
    expect(m.onEvent).toHaveBeenCalledWith(expect.objectContaining({ type: "consent_status" }));
    expect(m.onEvent).toHaveBeenCalledWith(expect.objectContaining({ type: "preview_card", preview: expect.objectContaining({ status: "Submitted" }) }));
  });

  it("a landed result settles the step the consent was for", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(pending());
        yield status(ended("Approved"));
        yield done(result("landed", { txIds: ["TXL"] }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("Completed");
  });

  it.each([
    ["denied on the phone", ended("Denied"), result("failed", { message: "server text" }), "Cancelled", "rejected on your phone".toLowerCase(), "You rejected this on your phone"],
    ["timed out", ended("TimedOut"), result("failed", { message: "server text" }), "Expired", "timed out", "The approval timed out"],
    ["wrong code three times", ended("Denied"), result("failed", { errorCode: "code_failed" }), "Cancelled", "rejected", "binding code was entered wrongly"],
  ])("%s: its own text, the card stops waiting, the code is gone", async (_name, state, outcome, previewStatus, _badge, noteStart) => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(pending());
        yield status(state);
        yield done(outcome);
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe(previewStatus);
    expect(m.entry().note).toContain(noteStart);
    expect(m.entry().consent!.bindingCode).toBeNull();
    expect(m.entry().consent!.state).toBe(state.state);
  });

  it("a final status with no outcome still ends the card", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Denied"));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("Cancelled");
    expect(consent.poll).not.toHaveBeenCalled();
  });

  it("an approved consent whose resume failed shows the reason and leaves the waiting state", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Approved"));
        yield done(result("failed", { errorCode: "resume_failed", message: "x" }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("Failed");
    expect(m.entry().note).toContain("could not be used");
  });

  it("no_active_device on the confirm answer says a phone must be set up", async () => {
    const consent = fakeConsent();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    m.confirm.mockResolvedValueOnce(result("failed", { errorCode: "no_active_device", message: "Enroll the Biatec authenticator" }));
    await m.chat.confirmPreview("p1");
    expect(m.entry().note).toContain("no phone is set up");
    expect(consent.stream).not.toHaveBeenCalled();
  });

  it("an unknown reason falls back to the backend's message", async () => {
    const m = await mountChat(fakeConsent(), [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    m.confirm.mockResolvedValueOnce(result("failed", { errorCode: "something_new", message: "The backend says so" }));
    await m.chat.confirmPreview("p1");
    expect(m.entry().note).toBe("The backend says so");
  });

  it("a second round of consent (the first approval could not be used) is followed, three more rounds at most", async () => {
    let round = 0;
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        round++;
        yield status(pending());
        yield status(ended("Approved"));
        yield done(result("consent_pending", { consent: pending({ bindingCode: "111111" }) }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await advance(1_000);
    expect(round).toBe(4); // the first, then three more rounds
    expect(consent.stream).toHaveBeenCalledTimes(4);
    // A fourth new round is not followed: the card stops waiting and says it cannot be followed any more.
    expect(m.entry().preview.status).toBe("Expired");
    expect(m.entry().note).toContain("can no longer be followed");
  });

  it("closes the connection of a stream attempt when it ends, however it ends", async () => {
    let signalSeen: AbortSignal | undefined;
    const consent = fakeConsent({
      stream: vi.fn(async function* (_id: string, signal?: AbortSignal) {
        signalSeen = signal;
        yield status(ended("Approved"));
        yield done(result("submitted", { txIds: ["TX"] }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("Submitted");
    expect(signalSeen?.aborted).toBe(true);
  });

  it("the backend's phone-approval prediction is loaded once when the card is shown; a failure shows nothing", async () => {
    const ok = fakeConsent({ prediction: vi.fn(async () => "Above your threshold: approval on your phone is needed") });
    const a = await mountChat(ok, [{ type: "preview_card", preview: preview() }]);
    expect(ok.prediction).toHaveBeenCalledTimes(1);
    // A card re-sent while still awaiting confirmation does not ask again once it has the text.
    await a.chat.send("again");
    expect(ok.prediction).toHaveBeenCalledTimes(1);
    expect(a.entry().prediction).toBe("Above your threshold: approval on your phone is needed");
    a.wrapper.unmount();

    const bad = fakeConsent({ prediction: vi.fn(async () => Promise.reject(new Error("down"))) });
    const b = await mountChat(bad, [{ type: "preview_card", preview: preview() }]);
    wrapper = b.wrapper;
    expect(b.entry().prediction ?? null).toBeNull();
  });

  it("an approval whose outcome has not arrived yet keeps being followed, and the poll brings the outcome", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Approved")); // the stream drops before confirm_result
      }),
      poll: vi.fn(async (): Promise<ConsentPoll> => ({ ok: true, consent: ended("Approved"), result: result("submitted", { txIds: ["TXA"] }) })),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await advance(2_000);
    expect(consent.poll).toHaveBeenCalledTimes(1);
    expect(m.entry().preview.status).toBe("Submitted");
  });

  it("something unexpected thrown while applying an answer does not leave the card waiting", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Approved"));
        yield done(result("submitted", { txIds: ["TX"] }));
      }),
    });
    let calls = 0;
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }], {
      onEvent: vi.fn(() => {
        if (++calls > 1) throw new Error("subscriber broke"); // the first call is the earlier preview card
      }),
    });
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).not.toBe("AwaitingApproval");
  });

  it("an outcome that is not about the action (sign-in lost, agent paused) leaves the card confirmable, not failed", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Approved"));
        yield done(result("sign_in_required"));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("AwaitingConfirmation");
    expect(m.entry().needsSignIn).toBe(true);
    expect(m.entry().consent).toBeNull();
  });

  it("failed cancels do not use up the rounds of consent", async () => {
    const consent = fakeConsent();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    for (let i = 0; i < 5; i++) {
      m.cancelPreview.mockResolvedValueOnce(false);
      await m.chat.cancelPreview("p1");
      await settle();
    }
    expect(m.entry().preview.status).toBe("AwaitingApproval");
    expect(m.entry().note).not.toContain("can no longer be followed");
  });

  it("an approval that ends needing the signing scope starts the authorize step", async () => {
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(ended("Approved"));
        yield done(result("scope_required", { reauthorizeScope: "sign" }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(m.entry().preview.status).toBe("AwaitingScope");
    expect(open).toHaveBeenCalled();
    expect(m.chat.phase.value).toBe("blocked");
  });

  it("the card shows the prediction in place of the generic line, only while awaiting confirmation", () => {
    const later = mount(ActionPreviewCard, { props: { preview: preview("AwaitingApproval"), prediction: "Probably needs your phone" } });
    expect(later.get("[data-test=phone-line]").text()).not.toContain("Probably needs your phone");
    later.unmount();
    const w = mount(ActionPreviewCard, { props: { preview: preview(), prediction: "Probably needs your phone" } });
    expect(w.get("[data-test=phone-line]").text()).toContain("Probably needs your phone");
    const plain = mount(ActionPreviewCard, { props: { preview: preview() } });
    expect(plain.get("[data-test=phone-line]").text()).toContain("Approval on your phone is expected");
    w.unmount();
    plain.unmount();
  });
});

// ---- fallback polling ---------------------------------------------------------------------------------------------------

describe("when the stream fails", () => {
  it("polls with 2, 4, 8, 15 second steps, one request at a time, and retries the stream after a minute", async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        throw new TransportError("down", 503);
      }),
      poll: vi.fn(async (): Promise<ConsentPoll> => {
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        await new Promise((r) => setTimeout(r, 500)); // a slow answer
        inFlight--;
        return { ok: true, consent: pending(), result: null };
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    expect(consent.stream).toHaveBeenCalledTimes(1);
    expect(consent.poll).not.toHaveBeenCalled();

    await advance(2_000); // first step: 2 s
    expect(consent.poll).toHaveBeenCalledTimes(1);
    await advance(500 + 4_000); // the answer, then 4 s
    expect(consent.poll).toHaveBeenCalledTimes(2);
    await advance(500 + 8_000);
    expect(consent.poll).toHaveBeenCalledTimes(3);
    await advance(500 + 15_000);
    expect(consent.poll).toHaveBeenCalledTimes(4);
    await advance(500 + 15_000); // capped at 15 s
    expect(consent.poll).toHaveBeenCalledTimes(5);
    expect(maxInFlight).toBe(1);

    await advance(30_000); // the minute is over: the stream is tried again
    expect(consent.stream.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it("a final answer from a poll ends the follower", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        throw new TransportError("down", 502);
      }),
      poll: vi.fn(async (): Promise<ConsentPoll> => ({ ok: true, consent: ended("Approved"), result: result("submitted", { txIds: ["TXP"] }) })),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await advance(2_000);
    expect(m.entry().preview.status).toBe("Submitted");
    const polls = consent.poll.mock.calls.length;
    await advance(120_000);
    expect(consent.poll.mock.calls.length).toBe(polls);
  });

  it("a poll that says 409 or 404 ends the follower with a clear note; 401 asks to sign in", async () => {
    for (const [http, text] of [
      [409, "can no longer be followed"],
      [401, "Sign in again"],
    ] as const) {
      const consent = fakeConsent({
        stream: vi.fn(async function* () {
          throw new TransportError("x", http);
        }),
      });
      const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
      await m.chat.confirmPreview("p1");
      await settle();
      expect(m.entry().preview.status).toBe("Expired");
      expect(m.entry().note).toContain(text);
      expect(m.entry().needsSignIn).toBe(http === 401);
      expect(consent.poll).not.toHaveBeenCalled();
      m.wrapper.unmount();
    }
  });

  it("gives up ten seconds after the consent's expiry and says it timed out", async () => {
    const expiresAt = iso(Date.now() + 20_000);
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        throw new TransportError("down", 503);
      }),
      poll: vi.fn(async (): Promise<ConsentPoll> => ({ ok: true, consent: pending({ expiresAt }), result: null })),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    m.confirm.mockResolvedValueOnce(result("consent_pending", { consent: pending({ expiresAt }) }));
    await m.chat.confirmPreview("p1");
    await advance(29_000);
    expect(m.entry().preview.status).toBe("AwaitingApproval");
    await advance(2_000); // past expiry + 10 s
    expect(m.entry().preview.status).toBe("Expired");
    expect(m.entry().consent).toMatchObject({ state: "TimedOut", bindingCode: null });
    expect(m.entry().note).toContain("timed out");
    const polls = consent.poll.mock.calls.length;
    await advance(60_000);
    expect(consent.poll.mock.calls.length).toBe(polls);
  });

  it("a stream that goes quiet is cut at the consent's deadline", async () => {
    const soon = () => pending({ expiresAt: iso(Date.now() + 20_000) });
    const consent = fakeConsent({
      stream: vi.fn(async function* (_id: string, signal?: AbortSignal) {
        yield status(soon());
        await open(signal); // never ends on its own
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    m.confirm.mockResolvedValueOnce(result("consent_pending", { consent: soon() }));
    await m.chat.confirmPreview("p1");
    await advance(31_000);
    expect(m.entry().preview.status).toBe("Expired");
  });

  it("does not poll while the tab has been hidden for more than 30 seconds, and polls once when it is visible again", async () => {
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        throw new TransportError("down", 503);
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    m.confirm.mockResolvedValueOnce(result("consent_pending", { consent: pending({ expiresAt: iso(Date.now() + 600_000) }) }));
    await m.chat.confirmPreview("p1"); // the follower starts here
    Object.defineProperty(document, "visibilityState", { value: "hidden", configurable: true });
    document.dispatchEvent(new Event("visibilitychange"));
    await advance(60_000);
    const whileHidden = consent.poll.mock.calls.length;
    await advance(120_000);
    expect(consent.poll.mock.calls.length).toBe(whileHidden); // nothing more once the pause began
    Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
    document.dispatchEvent(new Event("visibilitychange"));
    await settle();
    expect(consent.poll.mock.calls.length).toBe(whileHidden + 1);
  });
});

// ---- stopping ---------------------------------------------------------------------------------------------------------------

describe("the follower stops", () => {
  const failing = () =>
    fakeConsent({
      stream: vi.fn(async function* () {
        throw new TransportError("down", 503);
      }),
    });
  const quiet = async (consent: Fake) => {
    const polls = consent.poll.mock.calls.length;
    const streams = consent.stream.mock.calls.length;
    await advance(180_000);
    expect(consent.poll.mock.calls.length).toBe(polls);
    expect(consent.stream.mock.calls.length).toBe(streams);
  };

  it("on dispose", async () => {
    const consent = failing();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    await m.chat.confirmPreview("p1");
    await advance(3_000);
    m.wrapper.unmount();
    await quiet(consent);
  });

  it("on conversation reset (sign-out)", async () => {
    const consent = failing();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await advance(3_000);
    m.chat.resetConversation();
    await quiet(consent);
  });

  it("when the card's Cancel is pressed, and the open stream is aborted", async () => {
    let aborted = false;
    const consent = fakeConsent({
      stream: vi.fn(async function* (_id: string, signal?: AbortSignal) {
        yield status(pending());
        signal?.addEventListener("abort", () => (aborted = true));
        await open(signal);
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    await m.chat.cancelPreview("p1");
    await settle();
    expect(aborted).toBe(true);
    expect(m.entry().preview.status).toBe("Cancelled");
    expect(m.entry().consent!.bindingCode).toBeNull();
    await quiet(consent);
  });

  it("a failed cancel resumes following", async () => {
    const consent = fakeConsent();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    m.cancelPreview.mockResolvedValueOnce(false);
    await m.chat.cancelPreview("p1");
    await settle();
    expect(consent.poll).toHaveBeenCalled(); // one poll, then the stream again
    expect(m.entry().preview.status).toBe("AwaitingApproval");
  });

  it("there is never more than one follower for a preview", async () => {
    const consent = fakeConsent();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await m.chat.confirmPreview("p1"); // locked or not, no second follower
    await settle();
    expect(consent.stream).toHaveBeenCalledTimes(1);
  });
});

// ---- reload -------------------------------------------------------------------------------------------------------------------

describe("a card that is already waiting for the phone", () => {
  it("re-attaches: one poll first, then the stream", async () => {
    const order: string[] = [];
    const consent = fakeConsent({
      poll: vi.fn(async (): Promise<ConsentPoll> => (order.push("poll"), { ok: true, consent: pending(), result: null })),
      stream: vi.fn(async function* () {
        order.push("stream");
        yield status(pending());
        yield status(ended("Approved"));
        yield done(result("submitted", { txIds: ["TXR"] }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview("AwaitingApproval") }]);
    wrapper = m.wrapper;
    await settle();
    expect(order).toEqual(["poll", "stream"]);
    expect(m.entry().preview.status).toBe("Submitted");
  });

  it("an expired one shows timed out without a request", async () => {
    const consent = fakeConsent();
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview("AwaitingApproval", { expiresAt: iso(Date.now() - 60_000) }) }]);
    wrapper = m.wrapper;
    await settle();
    expect(consent.poll).not.toHaveBeenCalled();
    expect(consent.stream).not.toHaveBeenCalled();
    expect(m.entry().preview.status).toBe("Expired");
    expect(m.entry().consent!.state).toBe("TimedOut");
  });

  it("a poll that finds the consent already ended finishes the card", async () => {
    const consent = fakeConsent({
      poll: vi.fn(async (): Promise<ConsentPoll> => ({ ok: true, consent: ended("Denied"), result: result("failed") })),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview("AwaitingApproval") }]);
    wrapper = m.wrapper;
    await settle();
    expect(m.entry().preview.status).toBe("Cancelled");
    expect(consent.stream).not.toHaveBeenCalled();
  });

  it("nothing is followed without a consent service", async () => {
    const m = await mountChat(undefined, [{ type: "preview_card", preview: preview("AwaitingApproval") }]);
    wrapper = m.wrapper;
    await settle();
    expect(m.entry().preview.status).toBe("AwaitingApproval");
  });
});

// ---- the binding code -------------------------------------------------------------------------------------------------------------

describe("the binding code", () => {
  it("is never written to storage or the console, and is dropped when the consent ends", async () => {
    const spies = (["log", "info", "warn", "error", "debug"] as const).map((k) => vi.spyOn(console, k).mockImplementation(() => undefined));
    const consent = fakeConsent({
      stream: vi.fn(async function* () {
        yield status(pending());
        yield status(ended("Approved"));
        yield done(result("submitted", { txIds: ["TX"] }));
      }),
    });
    const m = await mountChat(consent, [{ type: "preview_card", preview: preview() }]);
    wrapper = m.wrapper;
    await m.chat.confirmPreview("p1");
    await settle();
    const dump = (s: Storage) => JSON.stringify(Object.fromEntries(Array.from({ length: s.length }, (_, i) => [s.key(i)!, s.getItem(s.key(i)!)])));
    expect(dump(localStorage)).not.toContain(CODE);
    expect(dump(sessionStorage)).not.toContain(CODE);
    for (const s of spies) expect(JSON.stringify(s.mock.calls)).not.toContain(CODE);
    expect(JSON.stringify(m.entry())).not.toContain(CODE);
  });
});

// ---- the transport pieces ------------------------------------------------------------------------------------------------------------

const enc = new TextEncoder();
function sseResponse(chunks: string[], status = 200): Response {
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      for (const ch of chunks) c.enqueue(enc.encode(ch));
      c.close();
    },
  });
  return new Response(body, { status, headers: { "Content-Type": "text/event-stream" } });
}
const json = (o: unknown, init = 200) => new Response(JSON.stringify(o), { status: init, headers: { "Content-Type": "application/json" } });
const cfg = (f: typeof fetch) => ({ backendUrl: "https://api.test", clientKey: "k", fetch: f });
const frame = (name: string, data: unknown) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;

describe("SseParser", () => {
  it("keeps the event name, across chunks and CRLF, and resets it per frame", () => {
    const p = new SseParser();
    const text = `event: consent_status\r\ndata: {"a":1}\r\n\r\ndata: {"b":2}\n\nevent: confirm_result\ndata: x\ndata: y\n\n`;
    const frames = [...p.pushFrames(text.slice(0, 20)), ...p.pushFrames(text.slice(20))];
    expect(frames).toEqual([
      { event: "consent_status", data: '{"a":1}' },
      { event: "", data: '{"b":2}' },
      { event: "confirm_result", data: "x\ny" },
    ]);
  });
  it("push() still returns only the data", () => {
    expect(new SseParser().push("event: e\ndata: d\n\n")).toEqual(["d"]);
  });
});

describe("ConsentClient", () => {
  afterEach(() => vi.useRealTimers());

  it("streams consent_status and confirm_result events and skips junk and unknown events", async () => {
    vi.useRealTimers();
    const f = vi.fn(async () =>
      sseResponse([
        frame("consent_status", { type: "consent_status", consent: { previewId: "p1", stepId: "s1", state: "Pending", bindingCode: CODE } }),
        "event: consent_status\ndata: not json\n\n",
        frame("consent_status", { type: "consent_status", consent: { previewId: "p1", state: "Bogus" } }),
        frame("mystery", { x: 1 }),
        frame("confirm_result", { status: "submitted", message: "", txIds: ["TX1"] }),
        frame("error", { error: "InvalidState", message: "gone" }),
      ]),
    );
    const out: ConsentStreamEvent[] = [];
    for await (const e of new ConsentClient(cfg(f as unknown as typeof fetch)).stream("p 1")) out.push(e);
    expect(out.map((e) => e.kind)).toEqual(["status", "result", "error"]);
    expect(out[1]).toMatchObject({ result: { status: "submitted", txIds: ["TX1"] } });
    const [url, init] = f.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("https://api.test/chat/previews/p%201/consent/stream");
    expect(init).toMatchObject({ method: "POST", credentials: "include", headers: expect.objectContaining({ "X-Biatec-Chat": "1", Accept: "text/event-stream" }) });
  });

  it("a non-2xx stream answer is a TransportError carrying the status", async () => {
    vi.useRealTimers();
    const c = new ConsentClient(cfg((async () => json({ error: "NotFound" }, 404)) as unknown as typeof fetch));
    await expect(c.stream("p1").next()).rejects.toMatchObject({ status: 404 });
  });

  it("poll returns the consent and the outcome, or the status of a refusal", async () => {
    vi.useRealTimers();
    const ok = new ConsentClient(
      cfg((async () => json({ consent: { previewId: "p1", stepId: "s1", state: "Approved" }, result: { status: "landed", txIds: ["T"] } })) as unknown as typeof fetch),
    );
    expect(await ok.poll("p1")).toMatchObject({ ok: true, consent: { state: "Approved" }, result: { status: "landed", txIds: ["T"] } });
    const gone = new ConsentClient(cfg((async () => json({ error: "NotFound", message: "no" }, 404)) as unknown as typeof fetch));
    expect(await gone.poll("p1")).toEqual({ ok: false, status: 404, message: "no" });
    const bad = new ConsentClient(cfg((async () => json({ consent: { nope: true } })) as unknown as typeof fetch));
    expect(await bad.poll("p1")).toMatchObject({ ok: false });
  });

  it("prediction returns the text, trimmed and bounded, and null on any failure", async () => {
    vi.useRealTimers();
    const text = new ConsentClient(cfg((async () => json({ text: "  Phone approval likely  " })) as unknown as typeof fetch));
    expect(await text.prediction("p1")).toBe("Phone approval likely");
    const long = new ConsentClient(cfg((async () => json({ text: "x".repeat(5000) })) as unknown as typeof fetch));
    expect((await long.prediction("p1"))!.length).toBe(300);
    expect(await new ConsentClient(cfg((async () => json({}, 401)) as unknown as typeof fetch)).prediction("p1")).toBeNull();
    expect(await new ConsentClient(cfg((async () => Promise.reject(new Error("net"))) as unknown as typeof fetch)).prediction("p1")).toBeNull();
    expect(await new ConsentClient(cfg((async () => json({ text: 5 })) as unknown as typeof fetch)).prediction("p1")).toBeNull();
  });

  it("toConfirmResult reads errorCode", () => {
    expect(toConfirmResult({ status: "failed", errorCode: "no_active_device", message: "m" }, 200)).toMatchObject({ errorCode: "no_active_device", message: "m" });
    expect(toConfirmResult(null, 401).status).toBe("sign_in_required");
  });
});
