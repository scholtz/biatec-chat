// Builds real unsigned Algorand transactions for tests, the conformance kit and demos of the mock: what a client such as the Biatec Chat
// backend hands to the signing endpoint (`transactions`, base64 msgpack, with the group id assigned and `grp`).
import { createHash } from "node:crypto";
import algosdk from "algosdk";

export interface TxSpec {
  sender: string;
  /** Payment (no `asset`, or asset 0) or asset transfer receiver. */
  receiver?: string;
  /** Asset id of an asset transfer; leave out for a payment in ALGO. */
  asset?: number;
  /** Base units (microalgos, or the asset's smallest unit). */
  amount?: bigint | number;
  /** An application call (no-op) to this application id; 0 creates one. */
  appId?: number;
  note?: string;
  /** What the mock must refuse (consent_unsupported_operation). */
  rekeyTo?: string;
  closeTo?: string;
  clawbackFrom?: string;
  /** A key registration (also refused). */
  keyreg?: boolean;
}

/** A valid Algorand address derived from a name, so a test names its accounts. */
export const account = (name: string): string => algosdk.encodeAddress(createHash("sha256").update("biatec-mock:" + name, "utf8").digest());

const suggestedParams = {
  fee: 1000,
  minFee: 1000,
  flatFee: true,
  firstValid: 1000,
  lastValid: 2000,
  genesisID: "testnet-v1.0",
  genesisHash: createHash("sha256").update("testnet-v1.0", "utf8").digest(),
};

function toTransaction(s: TxSpec): algosdk.Transaction {
  const common = { sender: s.sender, note: s.note === undefined ? undefined : new TextEncoder().encode(s.note), rekeyTo: s.rekeyTo, suggestedParams };
  if (s.keyreg) return algosdk.makeKeyRegistrationTxnWithSuggestedParamsFromObject({ ...common, nonParticipation: true });
  if (s.appId !== undefined) return algosdk.makeApplicationNoOpTxnFromObject({ ...common, appIndex: s.appId });
  if (s.asset !== undefined && s.asset !== 0) {
    return algosdk.makeAssetTransferTxnWithSuggestedParamsFromObject({ ...common, receiver: s.receiver ?? s.sender, assetIndex: s.asset, amount: s.amount ?? 0, closeRemainderTo: s.closeTo, assetSender: s.clawbackFrom });
  }
  return algosdk.makePaymentTxnWithSuggestedParamsFromObject({ ...common, receiver: s.receiver ?? s.sender, amount: s.amount ?? 0, closeRemainderTo: s.closeTo });
}

export interface BuiltGroup {
  /** Base64 msgpack unsigned transactions in group order, the body's `transactions`. */
  items: string[];
  /** Base64 group id (the body's `grp`); absent for one ungrouped transaction. */
  grp?: string;
  txns: algosdk.Transaction[];
}

/** Several transactions are grouped (the group id assigned); one stays ungrouped unless `grouped` says otherwise. */
export function buildGroup(specs: TxSpec[], opts: { grouped?: boolean } = {}): BuiltGroup {
  const txns = specs.map(toTransaction);
  const grouped = opts.grouped ?? txns.length > 1;
  if (grouped) algosdk.assignGroupID(txns);
  return {
    items: txns.map((t) => Buffer.from(algosdk.encodeUnsignedTransaction(t)).toString("base64")),
    grp: grouped ? Buffer.from(txns[0].group!).toString("base64") : undefined,
    txns,
  };
}

/** One USDC (31566704) transfer of `usdc` whole USDC: at the mock's price table 0.9225 EUR each. */
export const usdcTransfer = (sender: string, receiver: string, usdc: number | bigint, extra: Partial<TxSpec> = {}): TxSpec => ({ sender, receiver, asset: 31566704, amount: BigInt(usdc) * 1_000_000n, ...extra });
