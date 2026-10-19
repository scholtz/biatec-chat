// Mock "decoding" of an unsigned group. The real Biatec OIDC decodes Algorand transactions or a PSBT; the mock
// instead accepts base64(JSON) transaction descriptors so tests can state amounts and payees directly:
//   { "sender": "...", "receiver": "...", "application_id": 123, "asset": {"id","symbol","decimals"},
//     "amount": "5000.000000", "fiat_eur": "4612.50" }
// The group hash has the same shape as the real one (52 base32 characters for Algorand, 64 hex for Bitcoin) and
// covers every member of every descriptor, so any change to the group changes the hash (rule 5).
import { createHash } from "node:crypto";
import { jcs } from "../../scripts/lib.mjs";

export interface Asset { id: string; symbol: string; decimals: number }
export interface Tx { sender: string; receiver?: string; application_id?: number; asset: Asset; amount: string; fiat_eur: string }

const DECIMAL = /^(0|[1-9][0-9]*)(\.[0-9]+)?$/;
const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

export class GroupError extends Error {}

export function encodeTx(tx: Tx): string {
  return Buffer.from(JSON.stringify(tx), "utf8").toString("base64");
}

function base32(buf: Buffer): string {
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

/** The biatec_transaction authorization_details object, built only from the decoded group. */
export function transactionDetails(txs: Tx[], network: string) {
  const first = txs[0];
  let amount = "0";
  let fiat = "0";
  for (const t of txs) {
    amount = addDecimal(amount, t.amount);
    fiat = addDecimal(fiat, t.fiat_eur);
  }
  const details: Record<string, unknown> = {
    type: "biatec_transaction",
    network,
    group_hash: groupHash(txs, network),
    sender: first.sender,
    asset: first.asset,
    amount,
    fiat_value: { amount: fiat, currency: "EUR" },
  };
  const payee = txs.find((t) => t.receiver);
  if (payee?.receiver) details.receiver = payee.receiver;
  const app = txs.find((t) => t.application_id !== undefined);
  if (app) details.application_id = app.application_id;
  return details;
}
