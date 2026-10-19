import { algosdk } from "./lib.js";

/** Request parameters are untrusted JSON: every read checks its type and range and names the field on failure. */
export type Params = Record<string, unknown>;

export function asParams(params: unknown): Params {
  if (typeof params !== "object" || params === null || Array.isArray(params)) throw new Error("params must be an object");
  return params as Params;
}

const MAX_UINT64 = (1n << 64n) - 1n;

/** A uint64 given as a decimal string or a safe integer (amounts and ids can exceed Number.MAX_SAFE_INTEGER). */
export function uint64(p: Params, name: string, { positive = false } = {}): bigint {
  const v = p[name];
  let n: bigint;
  if (typeof v === "string" && /^\d{1,20}$/.test(v)) n = BigInt(v);
  else if (typeof v === "number" && Number.isSafeInteger(v) && v >= 0) n = BigInt(v);
  else throw new Error(`${name} must be a non-negative integer (decimal string or number)`);
  if (n > MAX_UINT64) throw new Error(`${name} is out of uint64 range`);
  if (positive && n === 0n) throw new Error(`${name} must be greater than zero`);
  return n;
}

export function address(p: Params, name: string): string {
  const v = p[name];
  if (typeof v !== "string" || !algosdk.isValidAddress(v)) throw new Error(`${name} must be an Algorand address`);
  return v;
}

export function optionalAddress(p: Params, name: string): string | undefined {
  return p[name] === undefined ? undefined : address(p, name);
}
