// What the mock does with an unsigned group, as the real Biatec OIDC does it (required-changes row 2 and 8):
//   - Algorand (the default): every item is a base64 msgpack unsigned transaction, decoded with algosdk. The group id is
//     recomputed from the handed transactions; an inconsistent `grp` or an inconsistent group is refused. The group hash of the
//     consent is that id (52 base32 characters; for one ungrouped transaction its transaction id).
//   - JSON groups (MOCK_JSON_GROUPS=1, option `jsonGroups`): base64(JSON) descriptors, so a test can state amounts and payees
//     without building transactions. Only the e2e demo harness still needs it. Its hash has the shape of the real one and nothing more.
// Both produce the same `Described` group, from which `transactionDetails` builds the 1.1 `biatec_transaction`.
import { createHash } from "node:crypto";
import algosdk from "algosdk";
import { jcs } from "../../scripts/lib.mjs";

export interface Asset { id: string; symbol: string; decimals: number }
/** The JSON-group descriptor. */
export interface Tx { sender: string; receiver?: string; application_id?: number; asset: Asset; amount: string; fiat_eur: string }

/** One outgoing transfer of the sender. `fiat` is the value in EUR with two decimals, absent when it could not be priced. */
export interface Transfer { receiver: string; asset: Asset; amount: string; fiat?: string }
export interface Described { groupHash: string; sender: string; transfers: Transfer[]; applications: number[] }

const DECIMAL = /^(0|[1-9][0-9]*)(\.[0-9]+)?$/;
const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
const MAX_LISTED = 16;

/** A refusal while decoding a group: `invalid_request` and `group_hash_mismatch` are 400, `consent_unsupported_operation` is 403. */
export class GroupError extends Error {
  code: string;
  status: number;
  constructor(message: string, code = "invalid_request") {
    super(message);
    this.code = code;
    this.status = code === "consent_unsupported_operation" ? 403 : 400;
  }
}

export function base32(buf: Uint8Array): string {
  let bits = "";
  let out = "";
  for (const b of buf) bits += b.toString(2).padStart(8, "0");
  bits = bits.padEnd(Math.ceil(bits.length / 5) * 5, "0");
  for (let i = 0; i < bits.length; i += 5) out += ALPHABET[parseInt(bits.slice(i, i + 5), 2)];
  return out;
}

export function addDecimal(a: string, b: string): string {
  const scale = Math.max(a.split(".")[1]?.length ?? 0, b.split(".")[1]?.length ?? 0);
  const toInt = (s: string) => {
    const [i, f = ""] = s.split(".");
    return BigInt(i + f.padEnd(scale, "0"));
  };
  const sum = (toInt(a) + toInt(b)).toString().padStart(scale + 1, "0");
  return scale === 0 ? sum : sum.slice(0, -scale) + "." + sum.slice(-scale);
}

// --- Valuation: the mock's own static price table (EUR per whole unit, in millionths). Real Biatec OIDC has none for the user's
// benefit to depend on; an asset that is not here is unpriced, so the group's value is unknown and the phone is asked. ---------------

const KNOWN: Record<string, { symbol: string; decimals: number; priceMicroEur: bigint }> = {
  "0": { symbol: "ALGO", decimals: 6, priceMicroEur: 200_000n },
  "31566704": { symbol: "USDC", decimals: 6, priceMicroEur: 922_500n },
  "10458941": { symbol: "USDC", decimals: 6, priceMicroEur: 922_500n },
};

const centsToDecimal = (cents: bigint) => `${cents / 100n}.${(cents % 100n).toString().padStart(2, "0")}`;
export const decimalToCents = (v: string) => {
  const [i, f = ""] = v.split(".");
  return BigInt(i) * 100n + BigInt((f + "00").slice(0, 2));
};

function rawToDecimal(raw: bigint, decimals: number): string {
  if (decimals === 0) return raw.toString();
  const s = raw.toString().padStart(decimals + 1, "0");
  return s.slice(0, -decimals) + "." + s.slice(-decimals);
}

function transferOf(receiver: string, assetId: string, raw: bigint): Transfer {
  const known = KNOWN[assetId];
  const asset: Asset = known ? { id: assetId, symbol: known.symbol, decimals: known.decimals } : { id: assetId, symbol: "ASA" + assetId, decimals: 0 };
  const t: Transfer = { receiver, asset, amount: rawToDecimal(raw, asset.decimals) };
  if (known) {
    const den = 10n ** BigInt(known.decimals) * 10_000n;
    t.fiat = centsToDecimal((raw * known.priceMicroEur + den / 2n) / den);
  }
  return t;
}

// --- Algorand ---------------------------------------------------------------------------------------------------------------

/**
 * Decodes and describes an unsigned Algorand group for `address`. Refuses what cannot be consented: a rekey, a close-to address, a
 * clawback and every transaction type other than payment, asset transfer and application call (403 consent_unsupported_operation),
 * a group id that does not match the transactions or a `grp` that differs from the recomputed id (400 group_hash_mismatch).
 */
export function describeAlgorandGroup(items: string[], address: string, grp?: string): { described: Described; txns: algosdk.Transaction[] } {
  const txns = items.map((item) => {
    try {
      const bytes = Buffer.from(item, "base64");
      if (bytes.length === 0) throw new Error("empty");
      return algosdk.decodeUnsignedTransaction(bytes);
    } catch {
      throw new GroupError("an item of the group is not a base64 msgpack unsigned Algorand transaction");
    }
  });
  if (txns.some((t) => t.sender.toString() !== address)) throw new GroupError("a signing call covers one sender address");

  for (const t of txns) {
    const unsupported = (why: string) => new GroupError(why, "consent_unsupported_operation");
    if (t.rekeyTo !== undefined) throw unsupported("a rekey cannot be consented to");
    if (t.type === algosdk.TransactionType.pay) {
      if (t.payment?.closeRemainderTo !== undefined) throw unsupported("a payment with a close-to address cannot be consented to");
    } else if (t.type === algosdk.TransactionType.axfer) {
      if (t.assetTransfer?.closeRemainderTo !== undefined || t.assetTransfer?.assetSender !== undefined) throw unsupported("an asset transfer with a close-to or clawback address cannot be consented to");
    } else if (t.type !== algosdk.TransactionType.appl) {
      throw unsupported(`a ${t.type} transaction cannot be consented to`);
    }
  }

  // The group id: recomputed from the handed transactions, which must carry it (more than one transaction without it is refused).
  const bare = txns.map((t) => {
    const c = algosdk.decodeUnsignedTransaction(algosdk.encodeUnsignedTransaction(t));
    c.group = undefined;
    return c;
  });
  let groupHash: string;
  if (txns.length === 1 && txns[0].group === undefined) {
    if (grp !== undefined) throw new GroupError("grp is given for a single ungrouped transaction", "group_hash_mismatch");
    groupHash = bare[0].txID();
  } else {
    const groupId = algosdk.computeGroupID(bare);
    if (txns.some((t) => t.group === undefined || Buffer.compare(Buffer.from(t.group), Buffer.from(groupId)) !== 0)) throw new GroupError("the group id of the transactions does not match the group", "group_hash_mismatch");
    if (grp !== undefined && grp !== Buffer.from(groupId).toString("base64")) throw new GroupError("grp does not match the handed transactions", "group_hash_mismatch");
    groupHash = base32(groupId);
  }

  const transfers: Transfer[] = [];
  const applications: number[] = [];
  for (const t of txns) {
    if (t.type === algosdk.TransactionType.pay) transfers.push(transferOf(t.payment!.receiver.toString(), "0", BigInt(t.payment!.amount)));
    else if (t.type === algosdk.TransactionType.axfer) transfers.push(transferOf(t.assetTransfer!.receiver.toString(), String(t.assetTransfer!.assetIndex), BigInt(t.assetTransfer!.amount)));
    else {
      const id = Number(t.applicationCall!.appIndex);
      if (!applications.includes(id)) applications.push(id);
    }
  }
  if (transfers.length > MAX_LISTED || applications.length > MAX_LISTED) throw new GroupError(`at most ${MAX_LISTED} transfers and ${MAX_LISTED} applications can be described`);
  return { described: { groupHash, sender: address, transfers, applications }, txns };
}

/** What signing yields in the mock: the transaction with an all-zero signature (it protects nothing, and decodes as a signed transaction). */
export function mockSigned(t: algosdk.Transaction): string {
  return Buffer.from(algosdk.encodeMsgpack(new algosdk.SignedTransaction({ txn: t, sig: new Uint8Array(64) }))).toString("base64");
}

// --- JSON groups (MOCK_JSON_GROUPS=1) ---------------------------------------------------------------------------------------------

export function encodeTx(tx: Tx): string {
  return Buffer.from(JSON.stringify(tx), "utf8").toString("base64");
}

export function decodeGroup(items: string[], sender: string): Tx[] {
  const txs = items.map((item) => {
    let tx: Tx;
    try {
      tx = JSON.parse(Buffer.from(item, "base64").toString("utf8"));
    } catch {
      throw new GroupError("unsigned_group item is not base64 JSON");
    }
    const ok =
      tx && typeof tx === "object" && typeof tx.sender === "string" && tx.asset && typeof tx.asset.id === "string" &&
      typeof tx.asset.symbol === "string" && Number.isInteger(tx.asset.decimals) &&
      DECIMAL.test(String(tx.amount)) && DECIMAL.test(String(tx.fiat_eur));
    if (!ok) throw new GroupError("unsigned_group item is not a valid transaction descriptor");
    return tx;
  });
  if (txs.some((t) => t.sender !== sender)) throw new GroupError("a signing call covers one sender address");
  return txs;
}

export function groupHash(txs: Tx[], network: string): string {
  const digest = createHash("sha256").update("group:" + jcs(txs), "utf8").digest();
  return network.startsWith("bitcoin") ? digest.toString("hex") : base32(digest);
}

export function describeJsonGroup(txs: Tx[], network: string): Described {
  const transfers: Transfer[] = [];
  const applications: number[] = [];
  for (const t of txs) {
    if (t.receiver) transfers.push({ receiver: t.receiver, asset: t.asset, amount: t.amount, fiat: centsToDecimal(decimalToCents(t.fiat_eur)) });
    if (t.application_id !== undefined && !applications.includes(t.application_id)) applications.push(t.application_id);
  }
  if (transfers.length + applications.length === 0) throw new GroupError("a descriptor needs a receiver or an application_id");
  return { groupHash: groupHash(txs, network), sender: txs[0].sender, transfers, applications };
}

// --- Details ----------------------------------------------------------------------------------------------------------------

/** The 1.1 `biatec_transaction` authorization_details object, built only from the decoded group. `trusted` is the user's own list for this client. */
export function transactionDetails(d: Described, network: string, origin: string, trusted: ReadonlySet<number>) {
  const applications = d.applications.map((id) => ({ id, trusted: id !== 0 && trusted.has(id) }));
  const transfers = d.transfers.map((t) => ({ receiver: t.receiver, asset: t.asset, amount: t.amount, ...(t.fiat !== undefined ? { fiat_value: { amount: t.fiat, currency: "EUR" } } : {}) }));
  const valueKnown = d.transfers.every((t) => t.fiat !== undefined) && applications.every((a) => a.trusted);
  const details: Record<string, unknown> = { type: "biatec_transaction", network, group_hash: d.groupHash, sender: d.sender, transfers, applications, value_known: valueKnown };
  let valueEur: string | null = null;
  if (valueKnown) {
    valueEur = centsToDecimal(d.transfers.reduce((sum, t) => sum + decimalToCents(t.fiat!), 0n));
    details.fiat_value = { amount: valueEur, currency: "EUR" };
  }
  details.origin = origin;
  return { details, valueEur };
}
