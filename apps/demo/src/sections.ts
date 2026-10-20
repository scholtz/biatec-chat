// The walkthrough, one entry per section of docs/demo-site.md. Every prompt is sent through useBiatecChat().ask() as a
// normal, visible user message to the real backend; nothing on this page is simulated. A capability that is not on
// stage yet is status "next" and has no prompt, so it can never look as if it worked.
//
// When a post-deploy check in docs/demo-site.md fails on stage, change that item's status here (and in the doc).

export type Status = "live" | "signed-in" | "next";

export interface Item {
  label: string;
  status: Status;
  /** Sent with ask(); only on items that work today. */
  prompt?: string;
  /** Why it is "next", or what to look for. */
  note?: string;
}

export interface Section {
  id: string;
  title: string;
  /** "What you're seeing". */
  seeing: string;
  items: Item[];
  /** Docs on GitHub, repository-relative. */
  docs?: { label: string; path: string }[];
}

export const STATUS_LABEL: Record<Status, string> = {
  live: "Works on stage",
  "signed-in": "Works on stage · sign in first",
  next: "Coming next",
};

export const SECTIONS: Section[] = [
  {
    id: "guest",
    title: "Guest tier: ask anything about Biatec",
    seeing:
      "You are not signed in. The widget talks to the backend's anonymous tier (Claude Sonnet 4.5 on Amazon Bedrock, " +
      "eu-central-1), which answers from the Biatec knowledge base. Wallet tools are withheld from the model and refused " +
      "server-side, so asking for a balance gets a sign-in card instead of an answer.",
    items: [
      { label: "What is Biatec AI?", status: "live", prompt: "What is Biatec AI and what can it do for me?" },
      {
        label: "Explain a Biatec Safe",
        status: "live",
        prompt: "How does a Biatec Safe with 2-of-2 approvals work on Algorand?",
      },
      {
        label: "Concentrated liquidity",
        status: "live",
        prompt: "What is concentrated liquidity on Biatec DEX, in two short paragraphs?",
      },
      {
        label: "Ask for a balance as a guest",
        status: "live",
        prompt: "What is my ALGO balance?",
        note: "Expect the sign-in card: the anonymous tier cannot call wallet tools.",
      },
    ],
    docs: [{ label: "Public chat contract", path: "docs/public-chat-contract.md" }],
  },
  {
    id: "sign-in",
    title: "Sign in, session tier and agent modes",
    seeing:
      "Sign in opens a popup at Biatec OIDC. The backend runs the OAuth code flow with PKCE and keeps the tokens " +
      "encrypted in its vault; this page only ever receives an HttpOnly session cookie for the backend host. After " +
      "sign-in the header shows your email and primary address, and the status pill shows the agent mode. Raising the " +
      "mode needs a confirm step; lowering it, Pause and Revoke are one click. The backend enforces the mode, not the UI.",
    items: [
      { label: "Ask to sign in", status: "live", prompt: "I'd like to sign in." },
      {
        label: "Agent modes: Analyze only, Prepare for approval, Pause",
        status: "signed-in",
        note: "Open the status pill in the widget header. In Analyze only, fund-moving tools are refused server-side.",
      },
      {
        label: "Rule-based automation",
        status: "next",
        note: "Shown disabled in the permissions dialog: the spec requires a confirmation for every action.",
      },
    ],
    docs: [{ label: "Security notes: logout and revocation", path: "docs/security-notes-logout-and-revocation.md" }],
  },
  {
    id: "data",
    title: "Data cards",
    seeing:
      "Read-only answers come back as cards instead of prose: a 24 hour market snapshot from public Biatec DEX data, " +
      "balances from wallet.balance through Biatec MCP, and your Biatec Safes from safe.list. Amounts are tabular and " +
      "every card names its network.",
    items: [
      { label: "Holdings card", status: "signed-in", prompt: "Show my holdings on Algorand testnet." },
      { label: "Safe card", status: "signed-in", prompt: "List my Biatec Safes on testnet." },
      {
        label: "Market snapshot card",
        status: "live",
        prompt: "Show me the ALGO/USDC market on Biatec DEX.",
        note: "Public Biatec DEX data, no sign-in needed. Facts only: no forecast or signal.",
      },
    ],
  },
  {
    id: "actions",
    title: "Actions on Algorand testnet",
    seeing:
      "Every action is a preview first: the backend builds the transaction group (Biatec MCP, the CLAMM SDK, algo-safe), " +
      "binds it to a group hash and shows fees, worst case and expiry. You review, then Confirm; the backend sends the " +
      "group straight to Biatec OIDC to sign, submits it and shows a receipt with explorer links. Testnet only on this site.",
    items: [
      {
        label: "Transfer",
        status: "signed-in",
        prompt: "Send 0.1 ALGO on testnet to my own primary address.",
      },
      { label: "Swap", status: "signed-in", prompt: "Swap 1 ALGO to USDC on Biatec DEX testnet." },
      {
        label: "Add liquidity",
        status: "signed-in",
        prompt: "Add 1 ALGO of liquidity to a Biatec DEX ALGO/USDC pool on testnet.",
      },
      {
        label: "Remove liquidity",
        status: "signed-in",
        prompt: "Remove my liquidity from the Biatec DEX ALGO/USDC pool on testnet.",
      },
      {
        label: "Safe: create",
        status: "signed-in",
        prompt: "Create a Biatec Safe on testnet with me as the only owner and 1-of-1 approvals.",
      },
      {
        label: "Safe: proposals, approve, execute",
        status: "signed-in",
        prompt: "Show pending proposals in my Biatec Safes on testnet.",
      },
      {
        label: "Phone approval in Biatec Authenticator above the threshold",
        status: "next",
        note:
          "Needs the consent endpoints at Biatec OIDC (docs/required-changes-biatec-oidc.md). Shown today against the " +
          "mock OIDC in docs/hackathon-demo.md, not on stage.",
      },
    ],
    docs: [
      { label: "Hackathon demo script", path: "docs/hackathon-demo.md" },
      { label: "Required changes to Biatec OIDC", path: "docs/required-changes-biatec-oidc.md" },
    ],
  },
  {
    id: "evidence",
    title: "Evidence you can verify offline",
    seeing:
      "Every analysis, preview, confirmation and receipt is a signed entry in a hash chain, anchored on Algorand every " +
      "ten minutes. Download the evidence bundle from a receipt card, then verify it without the backend using the " +
      "published JWKS. The Activity panel also exports or erases your own conversations.",
    items: [
      {
        label: "Download evidence from a receipt card",
        status: "signed-in",
        note: "After an action in section 4, use Download evidence on its receipt.",
      },
      {
        label: "Export or erase your conversations",
        status: "signed-in",
        note: "Open the Activity panel from the widget header: Your conversations → Download evidence, or Erase my data.",
      },
      { label: "Ask how the evidence is protected", status: "live", prompt: "How does Biatec Chat make its evidence log tamper-evident?" },
    ],
    docs: [{ label: "Threat model", path: "docs/threat-model.md" }],
  },
];
