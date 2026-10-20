// Types for the reference helpers in ../../scripts/lib.mjs (owned by T-046).
declare module "*/scripts/lib.mjs" {
  export function jcs(value: unknown): string;
  export function b64u(buf: Uint8Array): string;
  export function sha256Hex(text: string): string;
  export function thumbprint(jwk: { crv: string; kty: string; x: string; y: string }): string;
  export function signEs256(privateJwk: object, bytes: Uint8Array): string;
  export function signCompactJws(privateJwk: object, header: object, payload: object): string;
  export function verifyEs256(publicJwk: object, bytes: Uint8Array, signatureB64u: string): boolean;
  export function verifyReceipt(receipt: any, jwks: { keys: any[] }, opts: { issuer: string; now?: number }): boolean;
  export function verifyApproval(
    receipt: any,
    jwks: { keys: any[] },
    request: any,
    expectedBindingCode: string,
    consumed: Set<string>,
    opts: { issuer: string; now?: number },
  ): boolean;
  export const ATTESTATION_TYP: string;
}
