import type { SessionTier } from "@biatec/chat-protocol";

export interface SamplePrompt {
  label: string;
  prompt: string;
  minTier: SessionTier;
}

/** The spec's sample prompts. Anonymous users get what works without an account; the rest need sign-in. */
export const DEFAULT_PROMPTS: SamplePrompt[] = [
  { label: "What is the blockchain?", prompt: "Explain to me what the blockchain is and why Biatec uses Algorand.", minTier: "Anonymous" },
  {
    label: "Move Bitcoin between my addresses",
    prompt: "Transfer funds from my primary Bitcoin address to my secondary Bitcoin address.",
    minTier: "Authenticated",
  },
  { label: "Add liquidity", prompt: "Add liquidity to the Biatec DEX EURS/USDC pool.", minTier: "Authenticated" },
  { label: "Swap USDC to ALGO", prompt: "Swap 50 USDC to ALGO.", minTier: "Authenticated" },
  { label: "Create a Biatec Safe", prompt: "Create a Biatec Safe for my team with 2-of-3 approvals.", minTier: "Authenticated" },
  { label: "My spending limits", prompt: "What are my current spending limits?", minTier: "Authenticated" },
];

/** Prompts for the tier. A host-supplied list replaces the defaults and is shown to every tier. */
export function promptsFor(tier: SessionTier, option: "default" | string[]): SamplePrompt[] {
  if (Array.isArray(option)) return option.map((p) => ({ label: p, prompt: p, minTier: "Anonymous" as const }));
  return DEFAULT_PROMPTS.filter((p) => p.minTier === "Anonymous" || tier === "Authenticated");
}
