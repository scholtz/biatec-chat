import type { ChatTheme } from "../options";

/**
 * Result of `useBiatecChat().ask()`. Only `sent` means a message left the widget, and it is then visible as a user
 * message in the conversation.
 * - `busy`: a reply is still streaming (or another ask is starting); nothing was sent and the panel says so.
 * - `empty`: the prompt was blank.
 * - `unavailable`: no widget is mounted, or `mode: 'inline'` without a mounted `BiatecChatInline`, so the prompt
 *   could not be shown to the user and was not sent.
 */
export type AskResult = "sent" | "busy" | "empty" | "unavailable";

/** The host API returned by `useBiatecChat()`. */
export interface BiatecChatApi {
  /** Opens (or restores) the panel and moves focus to the composer. */
  open(): void;
  /** Closes the panel back to the launcher. */
  close(): void;
  /** Opens the panel and sends `prompt` as a normal, visible user message. */
  ask(prompt: string): Promise<AskResult>;
  /** Opt-in description of the current page, sent with later messages; `undefined` or `null` clears it. */
  setPageContext(context?: string | null): void;
  setTheme(theme: ChatTheme): void;
  setLocale(locale: string): void;
}
