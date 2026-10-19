import algosdk from "algosdk";
import {
  ACT_ACFG,
  ACT_APPL,
  ACT_AXFER,
  ACT_KEYREG,
  ACT_PAY,
  ACT_REKEY,
  ADM_ADD_MEMBER,
  ADM_CHANGE_THRESHOLD,
  ADM_CREATE_GROUP,
  ADM_REMOVE_MEMBER,
  ADM_SET_ACTIVE,
  ADM_SET_PAUSED,
  ADM_SET_POLICY,
  ADM_SET_PRIVILEGES,
  TX_ACFG,
  TX_APP,
  TX_ASSET,
  TX_KEYREG,
  TX_PAYMENT,
  TX_REKEY,
  ZERO_ADDR,
  decodeAppTxn,
  decodeAssetConfigTxn,
  decodeAssetTxn,
  decodeKeyRegTxn,
  decodePaymentTxn,
  decodeRekeyTxn,
  type AdminChange,
  type SafeTxnTuple,
} from "algo-safe";

/** A request the caller got wrong. Mapped to a build failure with a message that is safe to show. */
export class BadRequest extends Error {}

export type Json = null | boolean | number | string | Json[] | { [k: string]: Json };

/** bigint becomes a decimal string (amounts, asset ids and rounds may exceed 2^53), bytes become base64. */
export function toJson(value: unknown): Json {
  if (value === null || value === undefined) return null;
  if (typeof value === "bigint") return value.toString();
  if (value instanceof Uint8Array) return Buffer.from(value).toString("base64");
  if (Array.isArray(value)) return value.map(toJson);
  if (typeof value === "object") {
    return Object.fromEntries(Object.entries(value as Record<string, unknown>).map(([k, v]) => [k, toJson(v)]));
  }
  if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") return value;
  throw new BadRequest("unserialisable value");
}

export const ACTION_NAMES: [bigint, string][] = [
  [ACT_PAY, "payment"],
  [ACT_AXFER, "assetTransfer"],
  [ACT_APPL, "appCall"],
  [ACT_KEYREG, "keyReg"],
  [ACT_ACFG, "assetConfig"],
  [ACT_REKEY, "rekey"],
];

export function actionNames(mask: bigint): string[] {
  return ACTION_NAMES.filter(([bit]) => (mask & bit) !== 0n).map(([, n]) => n);
}

/** The allowedActions bit a payload entry needs from the proposing group. */
export function actionBitForTxType(txType: bigint): bigint {
  switch (txType) {
    case TX_PAYMENT:
      return ACT_PAY;
    case TX_ASSET:
      return ACT_AXFER;
    case TX_APP:
      return ACT_APPL;
    case TX_KEYREG:
      return ACT_KEYREG;
    case TX_ACFG:
      return ACT_ACFG;
    case TX_REKEY:
      return ACT_REKEY;
    default:
      throw new BadRequest(`unknown payload transaction type ${txType}`);
  }
}

const who = (addr: string): string => (addr === ZERO_ADDR ? "safe" : addr);

/** Decode stored payload tuples into a plain preview. Used for what we build and for what is read back from the chain. */
export function describePayload(tuples: SafeTxnTuple[]): Json[] {
  return tuples.map(([txType, data]) => {
    switch (txType) {
      case TX_PAYMENT: {
        const p = decodePaymentTxn(data);
        return toJson({ type: "payment", from: who(p.sender), receiver: p.receiver, amount: p.amount, closesAccountTo: p.hasClose ? p.closeRemainderTo : null, note: p.note });
      }
      case TX_ASSET: {
        const p = decodeAssetTxn(data);
        return toJson({ type: "assetTransfer", from: who(p.sender), assetId: p.xferAsset, receiver: p.assetReceiver, amount: p.assetAmount, closesAssetTo: p.hasClose ? p.assetCloseTo : null, note: p.note });
      }
      case TX_APP: {
        const p = decodeAppTxn(data);
        return toJson({ type: "appCall", appId: p.appId, onCompletion: p.onCompletion, appArgs: p.appArgs, accounts: p.accounts, foreignApps: p.foreignApps, foreignAssets: p.foreignAssets, note: p.note });
      }
      case TX_KEYREG: {
        const p = decodeKeyRegTxn(data);
        return toJson({ type: "keyReg", online: p.online === 1n, voteFirst: p.voteFirst, voteLast: p.voteLast });
      }
      case TX_ACFG: {
        const p = decodeAssetConfigTxn(data);
        return toJson({ type: "assetConfig", configAsset: p.configAsset, total: p.total, decimals: p.decimals, unitName: p.unitName, assetName: p.assetName, manager: p.manager, reserve: p.reserve, freeze: p.freeze, clawback: p.clawback });
      }
      case TX_REKEY: {
        const p = decodeRekeyTxn(data);
        return toJson({ type: "rekey", from: who(p.sender), rekeyTo: p.rekeyTo });
      }
      default:
        return toJson({ type: "unknown", txType });
    }
  });
}

const ADMIN_NAMES = new Map<bigint, string>([
  [ADM_CREATE_GROUP, "createGroup"],
  [ADM_ADD_MEMBER, "addMember"],
  [ADM_REMOVE_MEMBER, "removeMember"],
  [ADM_CHANGE_THRESHOLD, "changeThreshold"],
  [ADM_SET_POLICY, "setPolicy"],
  [ADM_SET_PRIVILEGES, "setPrivileges"],
  [ADM_SET_ACTIVE, "setActive"],
  [ADM_SET_PAUSED, "setPaused"],
]);

export function describeAdminChange(c: AdminChange): Json {
  return toJson({
    type: ADMIN_NAMES.get(c.changeType) ?? `admin-${c.changeType}`,
    targetGroupId: c.targetGroupId,
    groupName: c.groupName,
    member: c.memberAddr === ZERO_ADDR ? null : c.memberAddr,
    threshold: c.threshold,
    allowedActions: actionNames(c.allowedActions),
    adminPrivileges: c.adminPrivileges,
    dailyLimit: c.dailyLimit,
    monthlyLimit: c.monthlyLimit,
    active: c.activeFlag === 1n,
  });
}

// ---- input validation ----

export function obj(v: unknown, what: string): Record<string, unknown> {
  if (typeof v !== "object" || v === null || Array.isArray(v)) throw new BadRequest(`${what} must be an object`);
  return v as Record<string, unknown>;
}

export function address(v: unknown, what: string): string {
  if (typeof v !== "string" || !algosdk.isValidAddress(v)) throw new BadRequest(`${what} must be an Algorand address`);
  return v;
}

/** Non-negative integer from a JSON number or decimal string (strings are required above 2^53). */
export function uint(v: unknown, what: string, opts: { min?: bigint; max?: bigint } = {}): bigint {
  let n: bigint;
  if (typeof v === "bigint" && v >= 0n) n = v;
  else if (typeof v === "number" && Number.isSafeInteger(v) && v >= 0) n = BigInt(v);
  else if (typeof v === "string" && /^\d{1,20}$/.test(v)) n = BigInt(v);
  else throw new BadRequest(`${what} must be a non-negative integer (use a decimal string above 2^53)`);
  if (n < (opts.min ?? 0n) || n > (opts.max ?? (1n << 64n) - 1n)) throw new BadRequest(`${what} is out of range`);
  return n;
}

export function text(v: unknown, what: string, max: number, opts: { optional?: boolean } = {}): string {
  if (v === undefined || v === null) {
    if (opts.optional) return "";
    throw new BadRequest(`${what} is required`);
  }
  if (typeof v !== "string" || new TextEncoder().encode(v).length > max || (!opts.optional && v.length === 0)) {
    throw new BadRequest(`${what} must be a string of 1 to ${max} bytes`);
  }
  return v;
}
