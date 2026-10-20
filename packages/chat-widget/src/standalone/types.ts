// Public types of the standalone entry. Kept apart from index.ts so scripts/emit-types.mjs can compile them with tsc
// (they import only options.ts and api/types.ts), and the published declarations cannot drift from the source.
import type { BiatecChatOptions } from "../options";
import type { BiatecChatApi } from "../api/types";

/** Options of `mountBiatecChat`: the plugin options, plus where the widget's host element goes. */
export interface MountBiatecChatOptions extends Omit<BiatecChatOptions, "autoMount"> {
  /** Element (or CSS selector) the widget's host element is appended to. Default `document.body`. */
  container?: Element | string;
}

/** What `mountBiatecChat` returns: the host API plus `unmount()`. */
export interface BiatecChatHandle extends BiatecChatApi {
  /** The `<div data-biatec-chat-host>` element, or `null` without a DOM (server rendering). */
  readonly host: HTMLElement | null;
  /** Removes the widget from the page. A later `mountBiatecChat` mounts a fresh one. */
  unmount(): void;
}
