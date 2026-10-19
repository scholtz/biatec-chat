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
    return (this.opened ??= this.runProbe(true));
  }

  /** Call when the user clicks Sign in: retries the probe immediately unless the origin is unregistered. */
  async retryOnSignIn(): Promise<boolean> {
    if (this.active.kind === "cookie") return true;
    if (this.unregistered) return false;
    return (await this.runProbe(false)) === "success";
  }

  private async runProbe(first: boolean): Promise<ProbeResult> {
    if (await probeCookie(this.cfg)) {
      await this.switchToCookie();
      return "success";
    }
    if (first) {
      if (!(await this.pub.isOriginRegistered())) {
        this.unregistered = true;
        (this.opts.warn ?? console.warn)("BiatecChat: this origin is not registered for the tenant; using the public transport.");
        return "failed-unregistered";
      }
      this.scheduleRetry();
    }
    return "failed-registered";
  }

  private scheduleRetry(): void {
    if (this.retryTimer || this.active.kind === "cookie") return;
    const b = this.opts.backoffMs ?? DEFAULT_BACKOFF;
    const delay = b[Math.min(this.attempt++, b.length - 1)];
    this.retryTimer = setTimeout(async () => {
      this.retryTimer = null;
      if ((await this.runProbe(false)) !== "success") this.scheduleRetry();
    }, delay);
  }

  private async switchToCookie(): Promise<void> {
    if (this.active.kind === "cookie") return;
    const anon = this.pub.state;
    if (anon) await this.cookie.adopt(anon.conversationId, anon.continuationToken);
    this.pub.reset();
    this.active = this.cookie;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.opts.onSwitch?.("cookie");
  }

  send(o: SendOptions): AsyncGenerator<ChatEvent> {
    return this.active.send(o);
  }

  dispose(): void {
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.keepalive.stop();
  }
}
