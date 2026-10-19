import type { ChatEvent } from "@biatec/chat-protocol";
import { CookieTransport } from "./cookie";
import { Keepalive } from "./keepalive";
import { probeCookie, type ProbeResult } from "./probe";
import { PublicTransport } from "./public";
import type { ChatTransport, SendOptions, TransportConfig, TransportKind } from "./types";

export interface ManagerOptions {
  /** Delays between background probe retries (ms); the last value repeats. */
  backoffMs?: number[];
  warn?: (msg: string) => void;
  onSwitch?: (kind: TransportKind) => void;
}

const DEFAULT_BACKOFF = [5_000, 15_000, 45_000, 120_000];

/**
 * The one object the UI uses. Starts on the public transport; `open()` (first panel open, never page load) probes
 * the cookie transport and switches when it works.
 */
export class TransportManager implements ChatTransport {
  private readonly cookie: CookieTransport;
  private readonly pub: PublicTransport;
  private active: ChatTransport;
  private opened: Promise<ProbeResult> | null = null;
  private unregistered = false;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;
  private inflight: Promise<ProbeResult> | null = null;
  private disposed = false;
  readonly keepalive: Keepalive;

  constructor(
    private readonly cfg: TransportConfig,
    private readonly opts: ManagerOptions = {},
  ) {
    this.cookie = new CookieTransport(cfg);
    this.pub = new PublicTransport(cfg);
    this.active = this.pub;
    this.keepalive = new Keepalive(() => this.cookie.keepalive());
  }

  get kind(): TransportKind {
    return this.active.kind;
  }

  /** Call on first panel open. Idempotent. */
  open(): Promise<ProbeResult> {
    return (this.opened ??= this.probe(true));
  }

  /** Call when the user clicks Sign in: retries the probe immediately unless the origin is unregistered. */
  async retryOnSignIn(): Promise<boolean> {
    if (this.active.kind === "cookie") return true;
    if (this.unregistered) return false;
    return (await this.probe(false)) === "success";
  }

  /** At most one probe runs at a time; concurrent callers (timer, Sign in click, open) share its result. */
  private probe(first: boolean): Promise<ProbeResult> {
    if (!this.inflight) {
      this.inflight = this.runProbe(first).finally(() => {
        this.inflight = null;
      });
    }
    return this.inflight;
  }

  private async runProbe(first: boolean): Promise<ProbeResult> {
    if (await probeCookie(this.cfg)) {
      if (await this.switchToCookie()) return "success";
      // Adoption was refused: stay on the public transport (the conversation is kept) and try again later.
      this.scheduleRetry();
      return "failed-registered";
    }
    if (first) {
      if (!(await this.pub.isOriginRegistered())) {
        this.unregistered = true;
        (this.opts.warn ?? console.warn)("BiatecChat: this origin is not registered for the tenant; using the public transport.");
        return "failed-unregistered";
      }
    }
    this.scheduleRetry();
    return "failed-registered";
  }

  private scheduleRetry(): void {
    if (this.retryTimer || this.disposed || this.unregistered || this.active.kind === "cookie") return;
    const b = this.opts.backoffMs ?? DEFAULT_BACKOFF;
    const delay = b[Math.min(this.attempt++, b.length - 1)];
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.probe(false).catch(() => this.scheduleRetry());
    }, delay);
  }

  /** Returns true when the cookie transport is now active. Keeps the anonymous conversation if adoption fails. */
  private async switchToCookie(): Promise<boolean> {
    if (this.active.kind === "cookie") return true;
    const anon = this.pub.state;
    if (anon && !(await this.cookie.adopt(anon.conversationId, anon.continuationToken))) return false;
    this.pub.reset();
    this.active = this.cookie;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.opts.onSwitch?.("cookie");
    return true;
  }

  send(o: SendOptions): AsyncGenerator<ChatEvent> {
    return this.active.send(o);
  }

  dispose(): void {
    this.disposed = true;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.keepalive.stop();
  }
}
