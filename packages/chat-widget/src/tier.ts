import type { SessionTier } from "@biatec/chat-protocol";

/** Tier shown in the header; null until the session state is loaded (T-040). Internal, not part of the public typings. */
export type TierDisplay = SessionTier | null;
