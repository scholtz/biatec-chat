/** Interval timer that pings while something is pending (preview, consent, signing), even if the panel is closed. */
export const KEEPALIVE_INTERVAL_MS = 60_000;

export class Keepalive {
  private timer: ReturnType<typeof setInterval> | null = null;
  private pending = new Set<string>();

  constructor(
    private readonly ping: () => Promise<unknown>,
    private readonly intervalMs = KEEPALIVE_INTERVAL_MS,
  ) {}

  /** Mark a reason as pending; the timer runs while at least one reason is held. */
  hold(reason: string): void {
    this.pending.add(reason);
    if (!this.timer) this.timer = setInterval(() => void this.ping().catch(() => undefined), this.intervalMs);
  }

  release(reason: string): void {
    this.pending.delete(reason);
    if (!this.pending.size) this.stop();
  }

  stop(): void {
    this.pending.clear();
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  get running(): boolean {
    return this.timer !== null;
  }
}
