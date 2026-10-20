// Type-level parity of the hand-written dist/standalone/index.d.ts (scripts/emit-types.mjs) with the source entry
// src/standalone/index.ts: the same export names, and each export's type identical. Compiled after `pnpm build` by
// test/standalone.test.ts (`tsc -p test-types`); a mismatch is a compile error naming the export.
import type * as Src from "../src/standalone/index";
import type * as Pub from "../dist/standalone/index";

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
type Expect<T extends true> = T;

// Runtime exports: same names, same types.
export type Names = Expect<Equal<keyof typeof Src, keyof typeof Pub>>;
export type Mount = Expect<Equal<typeof Src.mountBiatecChat, typeof Pub.mountBiatecChat>>;
export type Unmount = Expect<Equal<typeof Src.unmount, typeof Pub.unmount>>;
export type AutoMount = Expect<Equal<typeof Src.autoMount, typeof Pub.autoMount>>;
export type UseChat = Expect<Equal<typeof Src.useBiatecChat, typeof Pub.useBiatecChat>>;
export type DarkAttr = Expect<Equal<typeof Src.DEFAULT_HOST_DARK_ATTRIBUTE, typeof Pub.DEFAULT_HOST_DARK_ATTRIBUTE>>;

// Type exports.
export type Opts = Expect<Equal<Src.MountBiatecChatOptions, Pub.MountBiatecChatOptions>>;
export type Handle = Expect<Equal<Src.BiatecChatHandle, Pub.BiatecChatHandle>>;
export type PluginOpts = Expect<Equal<Src.BiatecChatOptions, Pub.BiatecChatOptions>>;
export type Api = Expect<Equal<Src.BiatecChatApi, Pub.BiatecChatApi>>;
export type Ask = Expect<Equal<Src.AskResult, Pub.AskResult>>;
export type Theme = Expect<Equal<Src.ChatTheme, Pub.ChatTheme>>;
export type Mode = Expect<Equal<Src.ChatMode, Pub.ChatMode>>;
