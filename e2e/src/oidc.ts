// Harness around the mock Biatec OIDC (packages/consent-protocol/mock-oidc, T-047): the BFF side of a signing call
// (POST /wallet/{network}/{address}/sign, then CIBA token, then sign again) and the phone side (the authenticator
// fetching the request, a person typing the binding code, the device signing a receipt). The browser never appears
// here: like the real backend, this module is the only holder of OIDC bearer tokens.
import { readFileSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { jcs, signEs256 } from "../../packages/consent-protocol/scripts/lib.mjs";
import { createMockOidc } from "../../packages/consent-protocol/mock-oidc/src/app.ts";
import { encodeTx, type Tx } from "../../packages/consent-protocol/mock-oidc/src/group.ts";

export { encodeTx };
export type { Tx };

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "packages", "consent-protocol");
export const deviceKey: Record<string, string> = JSON.parse(readFileSync(join(root, "vectors", "keys.json"), "utf8")).device;

export const NETWORK = "algorand-testnet";
const CHAT_CLIENT = "Basic " + Buffer.from("biatec-chat:biatec-chat-secret").toString("base64");

export interface Identity {
  /** Demo name. */
  name: "Alice" | "Bob";
  /** Seeded mock user and device (the mock seeds `pw_carol`/`carol-device` and `pw_alice`/`alice-device`). */
  sub: string;
  deviceKid: string;
  deviceToken: string;
  /** The access token only the backend holds (scope `sign`). */
  accessToken: string;
  address: string;
}

export interface HttpResult { status: number; headers: Headers; json: any }

export interface Challenge { auth_req_id: string; binding_code: string; expires_in: number; [k: string]: unknown }

/** What the authenticator shows: the request as fetched from Biatec OIDC (it never contains the binding code). */
export interface PhoneCard { auth_req_id: string; authorization_details: any[]; client: any; binding_message: string; expiry: number; sub: string; [k: string]: unknown }

export type PhoneAction =
  | { decision: "approve"; code: string }
  | { decision: "deny" };

export class MockOidcHarness {
  readonly mock = createMockOidc();
  base = "";

  async start(): Promise<void> {
    await new Promise<void>((r) => this.mock.server.listen(0, "127.0.0.1", r));
    this.base = `http://127.0.0.1:${(this.mock.server.address() as AddressInfo).port}`;
  }

  async stop(): Promise<void> {
    this.mock.server.closeAllConnections();
    await new Promise<void>((r) => this.mock.server.close(() => r()));
  }

  /**
   * The demo precondition for the "always ask" fallback: both identities are "always ask" for the Biatec Chat client, have
   * not used it before (first use needs the phone) and have no spending limits that matter on testnet.
   */
  configureAlwaysAsk(...subs: string[]): void {
    for (const sub of subs) {
      const u = this.mock.state.users.get(sub)!;
      u.alwaysAsk = ["biatec-chat"];
      u.used.delete("biatec-chat");
    }
  }

  async call(method: string, path: string, opts: { auth?: string; body?: unknown; form?: Record<string, string> } = {}): Promise<HttpResult> {
    const headers: Record<string, string> = {};
    if (opts.auth) headers.Authorization = opts.auth;
    let body: string | undefined;
    if (opts.form) {
      headers["Content-Type"] = "application/x-www-form-urlencoded";
      body = new URLSearchParams(opts.form).toString();
    } else if (opts.body !== undefined) {
      headers["Content-Type"] = "application/json";
      body = JSON.stringify(opts.body);
    }
    const res = await fetch(this.base + path, { method, headers, body });
    return { status: res.status, headers: res.headers, json: await res.json() };
  }

  // --- the phone ---------------------------------------------------------------------------------------------------

  /** The authenticator opens a pending request: it fetches it from Biatec OIDC with the device token. */
  async phoneOpens(who: Identity, authReqId: string): Promise<PhoneCard> {
    const r = await this.call("GET", `/consent/requests/${authReqId}`, { auth: "Device " + who.deviceToken });
    if (r.status !== 200) throw new Error(`phone could not open the request: ${r.status} ${JSON.stringify(r.json)}`);
    return r.json;
  }

  /** The authenticator signs a receipt over the stored request, with the binding code the person typed. */
  async phoneAnswers(who: Identity, card: PhoneCard, action: PhoneAction): Promise<HttpResult> {
    const dev = (await this.call("GET", `/mock/devices/${who.deviceKid}`)).json;
    const signed = {
      authorization_details: card.authorization_details,
      auth_req_id: card.auth_req_id,
      client_entity_id: card.client.entity_id,
      binding_code: action.decision === "approve" ? action.code : "",
      decision: action.decision,
      expiry: card.expiry,
      sub: card.sub,
      binding_message: card.binding_message,
    };
    const receipt = {
      version: "1",
      signed,
      alg: "ES256",
      signature: signEs256(deviceKey, Buffer.from(jcs(signed), "utf8")),
      issued_at: Math.floor(Date.now() / 1000),
      device: { kid: dev.kid, public_key: dev.public_key, attestation: dev.attestation },
    };
    return this.call("POST", `/consent/requests/${card.auth_req_id}/receipt`, { auth: "Device " + who.deviceToken, body: receipt });
  }

  // --- the backend --------------------------------------------------------------------------------------------------

  /** First leg of a signing call: Biatec OIDC either signs (below threshold) or answers with a step-up challenge. */
  signRaw(bearer: string, who: Identity, group: Tx[], extra: object = {}): Promise<HttpResult> {
    return this.call("POST", `/wallet/${NETWORK}/${who.address}/sign`, { auth: "Bearer " + bearer, body: { unsigned_group: group.map(encodeTx), ...extra } });
  }

  /** The backend redeems an approved CIBA request for a one-time consent token and the signed receipt. */
  token(authReqId: string): Promise<HttpResult> {
    return this.call("POST", "/token", { auth: CHAT_CLIENT, form: { grant_type: "urn:openid:params:grant-type:ciba", auth_req_id: authReqId } });
  }
}
