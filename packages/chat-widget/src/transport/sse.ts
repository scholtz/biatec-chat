import type { ChatEvent } from "@biatec/chat-protocol";

export const CHAT_EVENT_TYPES = [
  "text_delta",
  "tool_status",
  "preview_card",
  "consent_status",
  "sign_in_card",
  "safe_card",
  "data_card",
  "error",
] as const;

const isStr = (v: unknown): v is string => typeof v === "string";
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null;
const isInt = (v: unknown): boolean => (typeof v === "number" && Number.isFinite(v)) || (isStr(v) && /^-?\d+$/.test(v));
/** Absent, or an array whose every element passes `ok`. */
const optArray = (v: unknown, ok: (x: unknown) => boolean): boolean => v === undefined || v === null || (Array.isArray(v) && v.every(ok));

const isSafeGroup = (g: unknown): boolean =>
  isObj(g) && isStr(g.name) && isInt(g.threshold) && isInt(g.memberCount) && optArray(g.allowedActions, isStr);
const isSafeProposal = (p: unknown): boolean =>
  isObj(p) &&
  isStr(p.proposalId) &&
  isStr(p.kind) &&
  isStr(p.status) &&
  isInt(p.approvals) &&
  isInt(p.threshold) &&
  isStr(p.proposer) &&
  isStr(p.payloadVersion) &&
  isStr(p.expiryRound);
const isSafe = (s: unknown): boolean =>
  isObj(s) &&
  isStr(s.appId) &&
  isStr(s.address) &&
  isStr(s.name) &&
  isStr(s.network) &&
  optArray(s.yourAddresses, isStr) &&
  optArray(s.signerGroups, isSafeGroup) &&
  optArray(s.pendingProposals, isSafeProposal);
/** Every field SafeCard.vue reads, so a malformed frame is dropped instead of breaking the message list. */
const isSafeCard = (c: unknown): boolean =>
  isObj(c) && isStr(c.source) && isStr(c.network) && Array.isArray(c.safes) && c.safes.every(isSafe) && optArray(c.couldNotRead, isStr);

/** Every field HoldingsCard.vue reads. Amounts must be strings so a number never loses digits above 2^53. */
const isHoldingsAsset = (a: unknown): boolean =>
  isObj(a) && isStr(a.assetId) && isStr(a.unitName) && isStr(a.amount) && isInt(a.decimals) && (a.fiatValue == null || isStr(a.fiatValue));
/** Every field MarketCard.vue reads; every number is a decimal string. */
const isDecimal = (v: unknown): boolean => isStr(v) && /^-?\d+(?:\.\d+)?$/.test(v);
const isMarketCard = (v: Record<string, unknown>): boolean =>
  v.kind === "market" &&
  isStr(v.asOf) &&
  isStr(v.pair) &&
  isDecimal(v.price) &&
  isDecimal(v.change24hPct) &&
  isDecimal(v.high24h) &&
  isDecimal(v.low24h) &&
  isDecimal(v.volume24h) &&
  Array.isArray(v.series) &&
  v.series.every(isDecimal) &&
  (v.source == null || isStr(v.source));
const isDataCard = (v: Record<string, unknown>): boolean =>
  isMarketCard(v) ||
  (v.kind === "holdings" &&
  isStr(v.network) &&
  isStr(v.asOf) &&
  Array.isArray(v.addresses) &&
  v.addresses.every(isStr) &&
  Array.isArray(v.assets) &&
  v.assets.every(isHoldingsAsset) &&
  (v.fiatCurrency == null || isStr(v.fiatCurrency)) &&
  (v.partial == null || typeof v.partial === "boolean"));

/** The statuses of an action preview and its steps (ActionPreviewStatus in the protocol). */
const PREVIEW_STATUSES = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval", "Signing", "Submitted", "Completed", "Cancelled", "Expired", "Failed"];
const CONSENT_STATES = ["NotRequired", "Pending", "Approved", "Denied", "TimedOut"];
/** Absent, null or a string. */
const optStr = (v: unknown): boolean => v === undefined || v === null || isStr(v);

const isAssetAmount = (a: unknown): boolean =>
  isObj(a) && isStr(a.assetId) && isStr(a.symbol) && isStr(a.amount) && isInt(a.decimals) && optStr(a.fiatValue) && optStr(a.fiatCurrency);
const isActionStep = (s: unknown): boolean =>
  isObj(s) &&
  isStr(s.stepId) &&
  isInt(s.index) &&
  isStr(s.title) &&
  isStr(s.status) &&
  PREVIEW_STATUSES.includes(s.status) &&
  isStr(s.groupHash) &&
  optStr(s.from) &&
  optStr(s.to) &&
  optArray(s.assets, isAssetAmount) &&
  optStr(s.feeAmount) &&
  optStr(s.slippagePercent) &&
  optStr(s.simulationSummary) &&
  optStr(s.transactionId) &&
  optStr(s.explorerUrl);
/** Every field ActionPreviewCard.vue reads. A malformed frame is dropped rather than breaking the message list (BCHAT-025). */
export const isActionPreview = (p: unknown): boolean =>
  isObj(p) &&
  isStr(p.previewId) &&
  isStr(p.capability) &&
  isStr(p.intent) &&
  isStr(p.network) &&
  isStr(p.genesisId) &&
  isStr(p.status) &&
  PREVIEW_STATUSES.includes(p.status) &&
  isStr(p.expiresAt) &&
  Array.isArray(p.steps) &&
  p.steps.every(isActionStep) &&
  (p.limit == null || (isObj(p.limit) && isStr(p.limit.bucket) && isStr(p.limit.remaining) && isStr(p.limit.currency))) &&
  (p.expectsAuthenticatorApproval == null || typeof p.expectsAuthenticatorApproval === "boolean") &&
  optStr(p.approvalReason);
export const isConsentStatus = (c: unknown): boolean =>
  isObj(c) && isStr(c.previewId) && isStr(c.stepId) && isStr(c.state) && CONSENT_STATES.includes(c.state) && optStr(c.bindingCode) && optStr(c.expiresAt);

/** Narrow an unknown JSON value to a ChatEvent: the discriminator and the required fields of that event type. */
export function isChatEvent(v: unknown): v is ChatEvent {
  if (!isObj(v) || !isStr(v.type)) return false;
  switch (v.type) {
    case "text_delta":
      return isStr(v.text);
    case "tool_status":
      return isStr(v.toolCallId) && isStr(v.label) && isStr(v.state);
    case "preview_card":
      return isActionPreview(v.preview);
    case "consent_status":
      return isConsentStatus(v.consent);
    case "sign_in_card":
      return isStr(v.signInUrl) && isStr(v.reason) && typeof v.unavailable === "boolean";
    case "safe_card":
      return isSafeCard(v.card);
    case "data_card":
      return isDataCard(v);
    case "error":
      return isStr(v.code) && isStr(v.message) && typeof v.retryable === "boolean";
    default:
      return false;
  }
}

/** Largest SSE frame (one event's data) or unterminated line the widget buffers. A real event is a few KiB (BCHAT-025). */
export const MAX_FRAME_CHARS = 1_000_000;

/** Incremental server-sent-events parser. Feed text chunks; it returns the data payloads completed so far. */
export class SseParser {
  private buffer = "";
  private data: string[] = [];
  private dataChars = 0;
  /** Set when a frame or line exceeded {@link MAX_FRAME_CHARS}; the parser then buffers nothing more. */
  overflowed = false;

  push(chunk: string): string[] {
    if (this.overflowed) return [];
    this.buffer += chunk;
    const out: string[] = [];
    for (;;) {
      const m = /\r\n|\n|\r/.exec(this.buffer);
      if (!m) break;
      // A lone trailing CR may be the first half of CRLF: wait for more input.
      if (m[0] === "\r" && m.index + 1 === this.buffer.length) break;
      const line = this.buffer.slice(0, m.index);
      this.buffer = this.buffer.slice(m.index + m[0].length);
      if (line === "") {
        if (this.data.length) out.push(this.data.join("\n"));
        this.data = [];
        this.dataChars = 0;
      } else if (line.startsWith("data:")) {
        this.dataChars += line.length;
        if (this.dataChars > MAX_FRAME_CHARS) return this.overflow();
        this.data.push(line.slice(5).replace(/^ /, ""));
      }
      // event:, id:, retry: and comment lines carry nothing we need: the JSON payload holds its own `type`.
    }
    // No line break yet and already too long: nothing legitimate looks like this.
    if (this.buffer.length > MAX_FRAME_CHARS) return this.overflow();
    return out;
  }

  private overflow(): string[] {
    this.overflowed = true;
    this.buffer = "";
    this.data = [];
    return [];
  }
}

/** Read a fetch response body as typed chat events. Malformed or unknown payloads are skipped, and an incomplete final frame is discarded (SSE spec). */
export async function* readChatEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<ChatEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  try {
    for (;;) {
      const { value, done } = await reader.read();
      const payloads = done ? [] : parser.push(decoder.decode(value, { stream: true }));
      if (parser.overflowed) {
        // End the stream with an error the UI already knows how to show, and stop reading the rest of the body.
        yield { type: "error", code: "stream_too_large", message: "The reply was too large and was stopped.", retryable: false } as ChatEvent;
        await reader.cancel().catch(() => undefined);
        return;
      }
      for (const p of payloads) {
        let parsed: unknown;
        try {
          parsed = JSON.parse(p);
        } catch {
          continue;
        }
        if (isChatEvent(parsed)) yield parsed;
      }
      if (done) return;
    }
  } finally {
    reader.releaseLock();
  }
}
