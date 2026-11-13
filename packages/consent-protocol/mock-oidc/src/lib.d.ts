// Types for the reference helpers in ../../scripts/lib.mjs (owned by T-046).
declare module "*/scripts/lib.mjs" {
  export function jcs(value: unknown): string;
  export function b64u(buf: Uint8Array): string;
  export function sha256Hex(text: string): string;
  export function thumbprint(jwk: { crv: string; kty: string; x: string; y: string }): string;
  export function signEs256(privateJwk: object, bytes: Uint8Array): string;
  export function signCompactJws(privateJwk: object, header: object, payload: object): string;
  export function verifyEs256(publicJwk: object, bytes: Uint8Array, signatureB64u: string): boolean;
  export type VerifyOptions = {
    issuer: string;
    now?: number;
    /** Read by verifyApproval only: the caller looked up that the signing device is active. A device attested for a key use (every 1.1 device) fails closed without `true`; verifyReceipt and verifyAttestation do not read it. */
    deviceActive?: boolean;
    /** Read by verifyDeviceSigned only: statement nonces already accepted from the device (single use). */
    usedNonces?: Set<string>;
  };
  export function verifyReceipt(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): boolean;
  export function verifyAttestation(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): boolean;
  export function attestedKeyUse(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): "approve" | "presence" | null;
  export function pairingCode(newApprovalThumbprint: string, newPresenceThumbprint: string, nonce: string): string;
  export function isDisplaySafe(text: unknown, max?: number): boolean;
  export function verifyDeviceSigned(
    envelope: any,
    jwks: { keys: any[] },
    opts: VerifyOptions,
    expect?: { approverActive?: boolean; sub?: string; approval?: { new_kid: string; new_approval_jwk_thumbprint: string; new_presence_jwk_thumbprint: string; nonce: string } },
  ): boolean;
  export function verifyEnrollmentProof(
    jws: string,
    expect: { approval_public_key: object; kid: string; presence_kid: string; presence_public_key: object; nonce: string; usedNonces: Set<string>; now?: number },
  ): boolean;
  export function verifyApproval(
    receipt: any,
    jwks: { keys: any[] },
    request: any,
    expectedBindingCode: string,
    consumed: Set<string>,
    opts: VerifyOptions,
  ): boolean;
  export const ATTESTATION_TYP: string;
  export const ENROLL_PROOF_TYP: string;
}
