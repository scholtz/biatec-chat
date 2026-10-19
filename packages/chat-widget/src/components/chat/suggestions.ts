import type { SessionTier } from "@biatec/chat-protocol";

export type PromptIcon = "book" | "layers" | "shield" | "sparkles" | "swap" | "droplets" | "wallet" | "gauge";

export interface SamplePrompt {
  label: string;
  prompt: string;
  minTier: SessionTier;
  icon: PromptIcon;
}

/**
 * The spec's sample prompts, four per audience for the 2 x 2 grid. Guests see what works without an account;
 * a signed-in session sees the wallet prompts.
 */
export const DEFAULT_PROMPTS: SamplePrompt[] = [
  { label: "What is the blockchain?", prompt: "Explain to me what the blockchain is and why Biatec uses Algorand.", minTier: "Anonymous", icon: "book" },
  { label: "How does Biatec DEX work?", prompt: "Explain how the Biatec DEX and its concentrated liquidity pools work.", minTier: "Anonymous", icon: "layers" },
  { label: "What is a Biatec Safe?", prompt: "What is a Biatec Safe and how do its approvals work?", minTier: "Anonymous", icon: "shield" },
  { label: "What can Biatec AI do?", prompt: "What can you help me with, and what needs me to sign in first?", minTier: "Anonymous", icon: "sparkles" },
  {
    label: "Move Bitcoin between my addresses",
    prompt: "Transfer funds from my primary Bitcoin address to my secondary Bitcoin address.",
    minTier: "Authenticated",
    icon: "wallet",
  },
  { label: "Add liquidity", prompt: "Add liquidity to the Biatec DEX EURS/USDC pool.", minTier: "Authenticated", icon: "droplets" },
  { label: "Swap USDC to ALGO", prompt: "Swap 50 USDC to ALGO.", minTier: "Authenticated", icon: "swap" },
  { label: "My spending limits", prompt: "What are my current spending limits?", minTier: "Authenticated", icon: "gauge" },
];

/** Prompts for the tier. A host-supplied list replaces the defaults and is shown to every tier. */
export function promptsFor(tier: SessionTier, option: "default" | string[]): SamplePrompt[] {
  if (Array.isArray(option)) return option.map((p) => ({ label: p, prompt: p, minTier: "Anonymous" as const, icon: "sparkles" as const }));
  const want: SessionTier = tier === "Authenticated" ? "Authenticated" : "Anonymous";
  return DEFAULT_PROMPTS.filter((p) => p.minTier === want);
}
