// The hackathon demo script (PRODUCT-DESCRIPTION.md, "Demonstration Script", steps 1-8) as a scripted API run in the
// "always ask" fallback mode: two identities (Alice, Bob), every signing call asks the phone, nobody has spending limits.
//
// What is real here: the Biatec OIDC consent protocol (the mock server validates every request and response against the
// published JSON schemas and verifies the device receipts), the unsigned-group hash binding, the single-use consent token,
// the signed consent receipts, and the offline evidence verifier. What is stubbed: the chat model, Biatec MCP, algod and
// the safe/DEX builders (the unsigned groups are the mock's transaction descriptors), and the phone (a scripted authenticator
// that signs receipts with the test device key). See e2e/README.md for the full list of gaps.
import { createHash } from "node:crypto";
import { verifyReceipt } from "../../packages/consent-protocol/scripts/lib.mjs";
import { groupHash } from "../../packages/consent-protocol/mock-oidc/src/group.ts";
import { ISSUER } from "../../packages/consent-protocol/mock-oidc/src/app.ts";
import type { EvidenceBundle } from "../../packages/evidence-verifier/src/verify.ts";
import { EvidenceKey, EvidenceRecorder } from "./evidence.ts";
import { NETWORK, type Identity, type MockOidcHarness, type PhoneCard, type Tx } from "./oidc.ts";

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
const algoAddr = (seed: string) => {
  let bits = "";
  for (const b of createHash("sha256").update(seed).digest()) bits += b.toString(2).padStart(8, "0");
  let out = "";
  for (let i = 0; out.length < 58; i += 5) out += ALPHABET[parseInt((bits + bits).slice(i % 250, (i % 250) + 5), 2)];
  return out;
};
const txIdOf = (blob: string) => {
  let bits = "";
  for (const b of createHash("sha256").update(blob).digest()) bits += b.toString(2).padStart(8, "0");
  bits = bits.padEnd(260, "0");
  let out = "";
  for (let i = 0; i < 260; i += 5) out += ALPHABET[parseInt(bits.slice(i, i + 5), 2)];
  return out.slice(0, 52);
};

const ALGO = { id: "0", symbol: "ALGO", decimals: 6 };
const EURS = { id: "31566704", symbol: "EURS", decimals: 6 };
const USDC = { id: "10458941", symbol: "USDC", decimals: 6 };
export const SAFE_APP_ID = 777;
export const POOL_APP_ID = 600;
export const SAFE_ADDRESS = algoAddr("safe-app-" + SAFE_APP_ID);
export const POOL_ADDRESS = algoAddr("pool-app-" + POOL_APP_ID);

export interface StepRecord {
  step: number;
  user: "Alice" | "Bob";
  chat: string;
  capability: string;
  extra: boolean;
  groupHash: string;
  authReqId: string | null;
  bindingCode: string | null;
  phone: "asked" | "not asked";
  phoneCard?: { amounts: string; fiatEur: string; receiver?: string; applicationId?: number; groupHash: string };
  wrongCodeAttempts: number;
  txIds: string[];
  notes: string[];
}

export interface DemoRun {
  steps: StepRecord[];
  bundles: { alice: EvidenceBundle; bob: EvidenceBundle };
  jwks: { keys: unknown[] };
  oidcJwks: { keys: any[] };
  issuer: string;
  /** Plain-language lines, in order, for the saved run log. */
  lines: string[];
  /** The group hashes a bait-and-switch was refused for. */
  refusedSwitches: string[];
}

interface Spec {
  step: number;
  chat: string;
  capability: string;
  group: Tx[];
  /** Marked (extra) in docs/hackathon-demo.md: the prompt disappears under testnet valuation. */
  extra?: boolean;
  /** The person types a wrong binding code first. */
  wrongCodeFirst?: boolean;
  /** A compromised backend tries to spend the approved consent on a different group first. */
  switchTo?: Tx[];
  note?: string;
}

class Session {
  readonly recorder: EvidenceRecorder;
  prompts = 0;
  constructor(readonly who: Identity, key: EvidenceKey) {
    this.recorder = new EvidenceRecorder(key);
    this.recorder.append("session_started", { tier: "signed-in", agentMode: "prepare", sub: who.sub, address: who.address });
  }
}

const tx = (sender: string, receiver: string, asset: Tx["asset"], amount: string, fiat: string, applicationId?: number): Tx => ({
  sender, receiver, asset, amount, fiat_eur: fiat, ...(applicationId === undefined ? {} : { application_id: applicationId }),
});

export async function runDemo(h: MockOidcHarness): Promise<DemoRun> {
  const alice: Identity = { name: "Alice", sub: "pw_carol", deviceKid: "carol-device", deviceToken: "device-token-carol", accessToken: "carol-chat-sign", address: algoAddr("alice") };
  const bob: Identity = { name: "Bob", sub: "pw_alice", deviceKid: "alice-device", deviceToken: "device-token-alice", accessToken: "alice-chat-sign", address: algoAddr("bob") };
  h.configureAlwaysAsk(alice.sub, bob.sub);

  const key = new EvidenceKey();
  const A = new Session(alice, key);
  const B = new Session(bob, key);
  const steps: StepRecord[] = [];
  const lines: string[] = [];
  const refusedSwitches: string[] = [];
  const log = (s: string) => lines.push(s);

  const cardSummary = (card: PhoneCard): StepRecord["phoneCard"] => {
    const d = card.authorization_details[0];
    return { amounts: `${d.amount} ${d.asset.symbol}`, fiatEur: d.fiat_value.amount, receiver: d.receiver, applicationId: d.application_id, groupHash: d.group_hash };
  };

  /** One Biatec OIDC signing call, end to end: preview, confirm, step-up, phone, consent token, signature, evidence. */
  async function signingCall(s: Session, spec: Spec): Promise<StepRecord> {
    const hash = groupHash(spec.group, NETWORK);
    const rec = s.recorder;
    const record: StepRecord = {
      step: spec.step, user: s.who.name, chat: spec.chat, capability: spec.capability, extra: spec.extra === true, groupHash: hash,
      authReqId: null, bindingCode: null, phone: "not asked", wrongCodeAttempts: 0, txIds: [], notes: spec.note ? [spec.note] : [],
    };
    log(`Step ${spec.step} | ${s.who.name}: "${spec.chat}" -> ${spec.capability}${spec.extra ? " (extra prompt)" : ""}`);
    rec.append("user_message", { text: spec.chat });
    rec.append("action_preview", { capability: spec.capability, groupHash: hash, network: NETWORK, sender: s.who.address, transactions: spec.group.length });
    rec.append("user_confirmed", { groupHash: hash });
    rec.append("signing_requested", { subjectHash: hash });

    const first = await h.signRaw(s.who.accessToken, s.who, spec.group);
    let signed: string[];
    if (first.status === 200) {
      signed = first.json.signed_group;
      record.notes.push("below threshold: signed without the phone");
      log(`  signed without the phone`);
    } else {
      if (first.status !== 401) throw new Error(`step ${spec.step}: expected a step-up challenge, got ${first.status} ${JSON.stringify(first.json)}`);
      const ch = first.json as { auth_req_id: string; binding_code: string };
      record.authReqId = ch.auth_req_id;
      record.bindingCode = ch.binding_code;
      log(`  step-up: auth_req_id ${ch.auth_req_id}, widget shows binding code ${ch.binding_code}`);

      // The phone: open the request, show the card, the person compares the code with the widget and types it.
      const card = await h.phoneOpens(s.who, ch.auth_req_id);
      if ("binding_code" in card) throw new Error("the phone request must never contain the binding code");
      record.phoneCard = cardSummary(card);
      log(`  phone card: ${record.phoneCard!.amounts} (${record.phoneCard!.fiatEur} EUR)${record.phoneCard!.applicationId !== undefined ? `, application ${record.phoneCard!.applicationId}` : ""}, hash ${record.phoneCard!.groupHash.slice(0, 8)}...`);
      if (card.authorization_details[0].group_hash !== hash) throw new Error(`step ${spec.step}: the phone shows a different group hash than the preview`);

      if (spec.wrongCodeFirst) {
        const wrong = ch.binding_code === "000000" ? "111111" : "000000";
        const r = await h.phoneAnswers(s.who, card, { decision: "approve", code: wrong });
        if (r.status !== 400 || r.json.error !== "wrong_code") throw new Error(`step ${spec.step}: a wrong code must be refused, got ${r.status}`);
        record.wrongCodeAttempts++;
        log(`  wrong code typed: refused, ${r.json.attempts_remaining} attempts left`);
      }
      const ok = await h.phoneAnswers(s.who, card, { decision: "approve", code: ch.binding_code });
      if (ok.status !== 200) throw new Error(`step ${spec.step}: the phone approval failed: ${ok.status} ${JSON.stringify(ok.json)}`);
      s.prompts++;
      record.phone = "asked";
      log(`  phone approved (prompt ${s.prompts} for ${s.who.name})`);

      const tok = await h.token(ch.auth_req_id);
      if (tok.status !== 200) throw new Error(`step ${spec.step}: token grant failed: ${tok.status} ${JSON.stringify(tok.json)}`);

      if (spec.switchTo) {
        const bad = await h.signRaw(tok.json.access_token, s.who, spec.switchTo);
        if (bad.status < 400 || bad.json.error !== "group_hash_mismatch") throw new Error(`step ${spec.step}: a different group must be refused with group_hash_mismatch, got ${bad.status} ${JSON.stringify(bad.json)}`);
        refusedSwitches.push(groupHash(spec.switchTo, NETWORK));
        record.notes.push("a different group was refused with the approved consent (group_hash_mismatch) and the consent stayed unspent");
        log(`  bait and switch refused: group_hash_mismatch, consent not spent`);
      }
      const second = await h.signRaw(tok.json.access_token, s.who, spec.group);
      if (second.status !== 200) throw new Error(`step ${spec.step}: signing with the consent failed: ${second.status} ${JSON.stringify(second.json)}`);
      signed = second.json.signed_group;

      const replay = await h.signRaw(tok.json.access_token, s.who, spec.group);
      if (replay.status < 400) throw new Error(`step ${spec.step}: the consent token must be single use`);
      record.notes.push("the consent token cannot be replayed");

      rec.append("consent_receipt", { stepSubjectHash: hash, authReqId: ch.auth_req_id, receipt: tok.json.consent_receipt });
    }
    record.txIds = signed.map((blob) => txIdOf(blob + "|" + steps.length)); // the real id also depends on the round and lease
    rec.append("signing_outcome", { subjectHash: hash, status: "signed", txIds: record.txIds, phone: record.phone });
    log(`  transaction ids: ${record.txIds.map((t) => t.slice(0, 10) + "...").join(", ")}`);
    steps.push(record);
    return record;
  }

  const a = alice.address;
  const b = bob.address;

  // 1. Alice creates a 2-of-2 safe with Bob. First action through Biatec Chat: the phone asks.
  await signingCall(A, {
    step: 1, chat: "Create a Biatec Safe for me and Bob with 2-of-2 approvals.", capability: "safe.create",
    group: [tx(a, SAFE_ADDRESS, ALGO, "0.300000", "0.06", SAFE_APP_ID)],
    note: "safe deployed; chat shows application " + SAFE_APP_ID + " and the safe address",
  });

  // 2. Alice funds the safe's minimum balance, then proposes and approves the opt-in (separate signing calls).
  await signingCall(A, {
    step: 2, chat: "Fund the safe with 2000 EURS and 2000 USDC. (ALGO for the minimum balance first)", capability: "wallet.transfer",
    group: [tx(a, SAFE_ADDRESS, ALGO, "2.000000", "0.40")], extra: true,
  });
  await signingCall(A, {
    step: 2, chat: "Yes, create the opt-in proposal.", capability: "safe.propose",
    group: [tx(a, SAFE_ADDRESS, ALGO, "0.000000", "0.00", SAFE_APP_ID)], extra: true,
  });
  await signingCall(A, {
    step: 2, chat: "Approve it as the first signer.", capability: "safe.approve",
    group: [tx(a, SAFE_ADDRESS, ALGO, "0.000000", "0.00", SAFE_APP_ID)], extra: true,
    note: "valued at zero (the stored proposal is the opt-ins)",
  });

  // 3. Bob approves as second signer and executes. His first action: the phone asks.
  await signingCall(B, {
    step: 3, chat: "Show me pending proposals in my safes. -> approve the setup proposal", capability: "safe.approve",
    group: [tx(b, SAFE_ADDRESS, ALGO, "0.000000", "0.00", SAFE_APP_ID)],
  });
  await signingCall(B, {
    step: 3, chat: "Execute it.", capability: "safe.execute",
    group: [tx(b, SAFE_ADDRESS, ALGO, "0.000000", "0.00", SAFE_APP_ID)], extra: true,
    note: "the safe opts in to EURS and USDC",
  });

  // 4. Alice funds the safe with 2000 EURS and 2000 USDC. A compromised backend tries to swap the payee after approval.
  const fund = [tx(a, SAFE_ADDRESS, EURS, "2000.000000", "2000.00"), tx(a, SAFE_ADDRESS, USDC, "2000.000000", "2000.00")];
  await signingCall(A, {
    step: 4, chat: "Now fund the safe.", capability: "wallet.transfer", group: fund, wrongCodeFirst: true,
    switchTo: [tx(a, algoAddr("mallory"), EURS, "2000.000000", "2000.00"), tx(a, algoAddr("mallory"), USDC, "2000.000000", "2000.00")],
    note: "the mock sums the two assets' amounts; the real Biatec OIDC lists each transfer on the card",
  });

  // 5. Top up with ALGO for the proposal box, then propose the liquidity (about 4000 EUR) and approve as first signer.
  await signingCall(A, {
    step: 5, chat: "Top up the safe with ALGO for the proposal box.", capability: "wallet.transfer",
    group: [tx(a, SAFE_ADDRESS, ALGO, "1.000000", "0.20")], extra: true,
  });
  await signingCall(A, {
    step: 5, chat: "Propose adding all the safe's EURS and USDC as liquidity to the Biatec DEX EURS/USDC pool.", capability: "safe.propose",
    group: [tx(a, SAFE_ADDRESS, ALGO, "0.100000", "0.02", SAFE_APP_ID)], extra: true,
    note: "the proposal itself moves nothing; payload built by biatec-concentrated-liquidity-amm, LP-token opt-in first",
  });
  await signingCall(A, {
    step: 5, chat: "Approve it as the first of two signers.", capability: "safe.approve",
    group: [tx(a, POOL_ADDRESS, EURS, "4000.000000", "4000.00", POOL_APP_ID)],
    note: "valued from the stored proposal: about 4000 EUR, shows the pool application",
  });

  // 6. Bob sees the proposal and approves: 2 of 2.
  await signingCall(B, {
    step: 6, chat: "Show me pending proposals. -> approve Alice's liquidity proposal", capability: "safe.approve",
    group: [tx(b, POOL_ADDRESS, EURS, "4000.000000", "4000.00", POOL_APP_ID)],
    note: "threshold met (2 of 2)",
  });

  // 7. Alice executes. The safe executes the inner group.
  await signingCall(A, {
    step: 7, chat: "Execute the proposal.", capability: "safe.execute",
    group: [tx(a, SAFE_ADDRESS, ALGO, "0.000000", "0.00", POOL_APP_ID)], extra: true,
    note: "chat shows the pool position and the LP tokens held by the safe",
  });

  // 8. The presenter exports the evidence of both sessions.
  const oidcJwks = (await h.call("GET", "/.well-known/jwks.json")).json;
  log(`Step 8 | evidence exported for both sessions (${A.recorder.entries.length} and ${B.recorder.entries.length} entries)`);
  return {
    steps,
    bundles: { alice: A.recorder.export(), bob: B.recorder.export() },
    jwks: key.jwks,
    oidcJwks,
    issuer: ISSUER,
    lines,
    refusedSwitches,
  };
}

/** Step 8's "lining up": hash chain, consent receipt and transaction ids of one session agree with the run log. */
export function crossCheck(bundle: EvidenceBundle, run: DemoRun, user: "Alice" | "Bob"): string[] {
  const problems: string[] = [];
  const mine = run.steps.filter((s) => s.user === user);
  const by = (kind: string) => bundle.entries.filter((e) => e.kind === kind).map((e) => e.payload as any);
  const requested = by("signing_requested");
  const outcomes = by("signing_outcome");
  const receipts = by("consent_receipt");
  if (requested.length !== mine.length) problems.push(`${requested.length} signing requests in the evidence, ${mine.length} in the run`);
  if (outcomes.length !== mine.length) problems.push(`${outcomes.length} signing outcomes in the evidence, ${mine.length} in the run`);
  const asked = mine.filter((s) => s.phone === "asked");
  if (receipts.length !== asked.length) problems.push(`${receipts.length} consent receipts in the evidence, ${asked.length} phone prompts in the run`);
  mine.forEach((s, i) => {
    if (requested[i]?.subjectHash !== s.groupHash) problems.push(`step ${s.step}: evidence hash differs from the run`);
    if (JSON.stringify(outcomes[i]?.txIds) !== JSON.stringify(s.txIds)) problems.push(`step ${s.step}: transaction ids differ`);
  });
  for (const r of receipts) {
    const rec = r.receipt;
    if (!verifyReceipt(rec, run.oidcJwks, { issuer: run.issuer, now: rec.issued_at })) problems.push(`receipt ${r.authReqId}: signature or attestation does not verify`);
    const details = rec.signed.authorization_details[0];
    if (details.group_hash !== r.stepSubjectHash) problems.push(`receipt ${r.authReqId}: approved hash differs from the signed group hash`);
    if (rec.signed.decision !== "approve") problems.push(`receipt ${r.authReqId}: not an approval`);
    const step = mine.find((s) => s.authReqId === r.authReqId);
    if (!step || step.groupHash !== details.group_hash) problems.push(`receipt ${r.authReqId}: no matching step`);
  }
  return problems;
}

export function renderRunLog(run: DemoRun, meta: { startedAt: string; verdicts: string[] }): string {
  const out: string[] = [];
  out.push("# Demo e2e run log (mock Biatec OIDC, always-ask fallback)");
  out.push("");
  out.push(`Run at ${meta.startedAt}. Mode: mock OIDC (T-047), no real network, no AWS.`);
  out.push("");
  out.push(...run.lines.map((l) => (l.startsWith("  ") ? l : "\n" + l)));
  out.push("");
  const alice = run.steps.filter((s) => s.user === "Alice" && s.phone === "asked").length;
  const bob = run.steps.filter((s) => s.user === "Bob" && s.phone === "asked").length;
  const extra = run.steps.filter((s) => s.extra).length;
  out.push(`Phone prompts: Alice ${alice}, Bob ${bob} (${extra} of ${run.steps.length} signing calls are the "extra" prompts that disappear under testnet valuation).`);
  out.push("");
  out.push("## Verification");
  out.push(...meta.verdicts.map((v) => "- " + v));
  out.push("");
  return out.join("\n");
}
