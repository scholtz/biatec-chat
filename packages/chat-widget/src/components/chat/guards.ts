/** Small shared checks for untrusted JSON from the backend. */
export const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null;
/** The value when it is a string, otherwise the empty string. */
export const str = (v: unknown): string => (typeof v === "string" ? v : "");
/** A non-negative decimal amount as the backend accepts it in a query: digits, optionally a fraction. */
export const DECIMAL_AMOUNT = /^\d{1,15}(?:\.\d{1,18})?$/;
