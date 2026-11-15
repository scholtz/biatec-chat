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
  export function verifyReceiptSignature(receipt: any): boolean;
  export function verifyReceipt(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): boolean;
  export function verifyAttestation(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): boolean;
  export function attestedKeyUse(receipt: any, jwks: { keys: any[] }, opts: VerifyOptions): "approve" | "presence" | null;
  export function pairingCode(newApprovalThumbprint: string, newPresenceThumbprint: string, nonce: string): string;
  export function isDisplaySafe(text: unknown, max?: number, opts?: { joiners?: boolean }): boolean;
  // Protocol 1.2 (relying parties), spec chapters 14 and 15.
  export const RP_TYPES: readonly ["biatec_payment", "biatec_login", "biatec_action"];
  export function detailsHash(details: unknown): string;
  export function clientFingerprint(clientId: string): string;
  export function isRelyingPartyText(text: unknown, max: number): boolean;
  export function isValidIban(iban: unknown): boolean;
  export function isValidPaymentAmount(amount: unknown): boolean;
  export function isValidDate(text: unknown): boolean;
  export function isValidRelyingPartyDetails(details: unknown): boolean;
  export function isValidLinkSigned(signed: unknown): boolean;
  export type Pin = { key_use: "approve" | "presence"; jwk: object } | object;
  export function verifyReceiptPinned(
    receipt: any,
    pinned: Pin | Pin[],
    expectedDetails: object,
    opts: { now?: number; origin: string | null; decision?: "approve" | "deny" | "code_failed"; auth_req_id?: string; sub?: string; clientFingerprint?: string; bindingCode?: string; consumed?: Set<string>; checkExpiry?: boolean },
  ): boolean;
  export function verifyLink(
    envelope: any,
    jwks: { keys: any[] },
    opts: { issuer: string; consumed: Set<string>; now?: number },
    expect: { link_id: string; client_fingerprint: string; domain: string | null; nonce: string; binding_code?: string; sub: string },
  ): boolean;
  export function verifyLinkPinned(
    approval: { signed: any; signature: string },
    devices: { kid: string; key_use: "approve" | "presence"; jwk: object }[],
    expect: { link_id: string; client_fingerprint: string; domain: string | null; nonce: string; binding_code?: string },
    opts?: { now?: number; consumed?: Set<string> },
  ): boolean;
  export function verifyDeviceSigned(
    envelope: any,
    jwks: { keys: any[] },
    opts: VerifyOptions & { usedNonces: Set<string> },
    expect: { approverActive: true; sub: string; approval?: { new_kid: string; new_approval_jwk_thumbprint: string; new_presence_jwk_thumbprint: string; nonce: string } },
  ): boolean;
  export function verifyEnrollmentProof(
    jws: string,
    expect: { approval_public_key: object; kid: string; presence_kid: string; presence_public_key: object; presence_proof: string; nonce: string; usedNonces: Set<string>; now?: number },
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
  export const ENROLL_PRESENCE_PROOF_TYP: string;
  export const KEY_USES: readonly ["approve", "presence"];
  export function attestationClaims(attestation: string, jwks: { keys: any[] }, opts: VerifyOptions): Record<string, any> | null;
  export const ENROLL_PROOF_TYP: string;
}

// The relying-party and phone helpers shared by the demo, the tests and the conformance kit (scripts/rp-client.mjs, plain JavaScript).
declare module "*/scripts/rp-client.mjs" {
  export const vectorKeys: Record<string, Record<string, string>>;
  export const CLIENT_ASSERTION_TYPE: string;
  export interface RpKey { alg: "ES256" | "PS256"; kid: string; privateJwk: Record<string, string>; publicJwk: Record<string, string> }
  export interface AssertionOptions { exp?: number; iat?: number; jti?: string; iss?: string; sub?: string; aud?: string | string[]; alg?: "ES256" | "PS256"; kid?: string | null; now?: number }
  export interface Reply { status: number; headers: Headers; json: any }
  export interface RelyingParty { base: string; issuer: string; clientId: string; key: RpKey; clock?: () => number }
  export interface TestDevice { auth: string; kid: string; publicKey: Record<string, string>; attestation: string; presence: { kid: string; public_key: Record<string, string>; attestation: string }; approvalKey?: Record<string, string>; presenceKey?: Record<string, string> }
  export function generateRpKey(alg?: "ES256" | "PS256", kid?: string): RpKey;
  export function clientAssertion(key: RpKey, clientId: string, audience: string, o?: AssertionOptions): string;
  export function httpJson(base: string, method: string, path: string, opts?: { headers?: Record<string, string>; body?: unknown; form?: Record<string, string> }): Promise<Reply>;
  export function rpCall(rp: RelyingParty, method: string, path: string, opts?: { body?: Record<string, unknown>; form?: Record<string, string>; assertion?: string; assertionOptions?: AssertionOptions }): Promise<Reply>;
  export function registerRelyingParty(base: string, issuer: string, o?: { clientUri?: string; alg?: "ES256" | "PS256"; extra?: Record<string, unknown> }): Promise<{ rp: RelyingParty; reply: Reply }>;
  export function proveDomain(rp: RelyingParty, host: string): Promise<Reply>;
  export function testDevice(base: string, user?: string, o?: { kid?: string; token?: string; approvalKey?: Record<string, string>; presenceKey?: Record<string, string> }): Promise<TestDevice>;
  export function parseLinkUri(uri: string): { iss: string; id: string; secret: string };
  export function approveLink(base: string, dev: TestDevice, linkUri: string, a: { code: string; decision?: "approve" | "deny"; presence?: boolean; change?: Record<string, unknown>; now?: number }): Promise<{ linkRequest: Reply; posted: Reply | undefined; signed?: Record<string, any> }>;
  export function approveRequest(base: string, dev: TestDevice, authReqId: string, a: { code: string; decision?: "approve" | "deny"; presence?: boolean; change?: Record<string, unknown>; now?: number }): Promise<{ fetched: Reply; receipt?: any; posted: Reply | undefined }>;
}
