// Protocol 1.2, relying parties (spec chapter 14, Biatec OIDC issue 38): registration with private_key_jwt, the client assertion, linking, the payment, login and action
// requests, verification with the pinned device key, and every refusal. The kit plays both the party (a key pair generated in memory) and the phone (the test device).
// Needs: `relyingParties` (the server may register throwaway parties: the in-process mock, or CONFORMANCE_RP_REGISTRATION=1); the cases that play the phone also need
// `mockHelpers` (the device's attestation and keys, and the stand-in for the domain proof) and `deviceKey`; the presence-key cases also `presenceKey`.
import { describe, it, expect, inject } from "vitest";
import { createHmac } from "node:crypto";
import { clientFingerprint, detailsHash, jcs, signEs256, verifyLink, verifyLinkPinned, verifyReceipt, verifyReceiptPinned } from "../../scripts/lib.mjs";
import { account, approveLink, approveRequest, buildGroup, config, http, httpJson, issuerOf, needs, newRelyingParty, phone, registerRelyingParty, rpCall, schemaProblems, parseLinkUri } from "../src/kit.ts";
import { usdcTransfer } from "../../mock-oidc/src/build.ts";
import { clientAssertion, generateRpKey } from "../../scripts/rp-client.mjs";
import type { Need } from "../src/needs.ts";

const base = inject("baseUrl");
const CIBA = "urn:openid:params:grant-type:ciba";
const registering: Need[] = ["relyingParties"];
const playing: Need[] = ["relyingParties", "mockHelpers", "deviceKey"];
const nowS = () => Math.floor(Date.now() / 1000);

const PAYMENT = { type: "biatec_payment", instructed_amount: { amount: "1250.00", currency: "EUR" }, creditor_name: "Muster Energie GmbH", creditor_account: { iban: "DE89370400440532013000" }, remittance: "Invoice 2026-0042", execution_date: "2026-10-20" };
const LOGIN = { type: "biatec_login", context: { ip: "203.0.113.42", location: "Bratislava, Slovakia", user_agent: "Firefox 133 on Windows 11" } };
const ACTION = { type: "biatec_action", title: "Add payee", lines: [{ label: "Payee", value: "Muster Energie GmbH" }, { label: "Account", value: "DE89 3704 0044 0532 0130 00" }] };
const ORIGIN = "https://netbanking.bank.example";

/** A party registered, linked on the phone and its approval verified: what it pinned. */
async function linkedParty(o: { prove?: boolean } = {}) {
  const { rp, host } = await newRelyingParty(base, { prove: o.prove ?? true });
  const created = await rpCall(rp, "POST", "/consent/links", { body: { user_label: "Alex M. (account ending 4421)" } });
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  expect(schemaProblems("link-created", created.json)).toEqual([]);
  const dev = await phone(base);
  const { linkRequest, posted } = await approveLink(base, dev, created.json.link_uri, { code: created.json.binding_code });
  expect(posted?.status, JSON.stringify(posted?.json ?? linkRequest.json)).toBe(200);
  const status = await rpCall(rp, "GET", `/consent/links/${created.json.link_id}`);
  expect(status.status, JSON.stringify(status.json)).toBe(200);
  expect(schemaProblems("link-status", status.json)).toEqual([]);
  return { rp, host, dev, created: created.json, linkRequest: linkRequest.json, status: status.json, subject: status.json.subject as string, pins: status.json.devices as { kid: string; key_use: "approve" | "presence"; jwk: object }[] };
}
type Party = Awaited<ReturnType<typeof linkedParty>>;

const open = (p: Party, details: unknown, extra: Record<string, unknown> = {}) => rpCall(p.rp, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: p.subject, authorization_details: [details], ...extra } });
const redeem = (p: Party, id: string) => rpCall(p.rp, "POST", "/token", { form: { grant_type: CIBA, auth_req_id: id } });

describe("registration (spec 14.2)", () => {
  it("a private_key_jwt client is registered, no secret is issued", needs(registering, async () => {
    const { rp, reply } = await registerRelyingParty(base, await issuerOf(base), { clientUri: "https://conformance-reg.example" });
    expect(reply.status, JSON.stringify(reply.json)).toBe(201);
    expect(reply.json.client_id).toBe(rp.clientId);
    expect(reply.json.token_endpoint_auth_method).toBe("private_key_jwt");
    expect(reply.json.grant_types).toEqual([CIBA]);
    expect(Object.keys(reply.json)).not.toContain("client_secret");
    expect(JSON.stringify(reply.json)).not.toContain(rp.key.privateJwk.d);
  }));

  const key = () => generateRpKey().publicJwk;
  const good = () => ({ token_endpoint_auth_method: "private_key_jwt", client_uri: "https://conformance-refused.example", jwks: { keys: [key()] } });
  const refused: [string, () => Record<string, unknown>, string?][] = [
    ["a public client (auth method none)", () => ({ ...good(), token_endpoint_auth_method: "none" })],
    ["a client with a secret", () => ({ ...good(), token_endpoint_auth_method: "client_secret_basic" })],
    ["an authorization_code grant", () => ({ ...good(), grant_types: [CIBA, "authorization_code"] })],
    ["a redirect URI", () => ({ ...good(), redirect_uris: ["https://conformance-refused.example/cb"] }), "invalid_redirect_uri"],
    ["a wallet scope", () => ({ ...good(), scope: "openid sign" })],
    ["no client_uri", () => ({ ...good(), client_uri: undefined })],
    ["an http client_uri", () => ({ ...good(), client_uri: "http://conformance-refused.example" })],
    ["an IP address as client_uri", () => ({ ...good(), client_uri: "https://203.0.113.9/" })],
    ["a jwks with a private member", () => ({ ...good(), jwks: { keys: [generateRpKey().privateJwk] } })],
    ["more than 4 keys", () => ({ ...good(), jwks: { keys: Array.from({ length: 5 }, (_, i) => ({ ...key(), kid: "k" + i })) } })],
    ["neither jwks nor jwks_uri", () => ({ ...good(), jwks: undefined })],
  ];
  for (const [name, body, error] of refused) {
    it(`${name} is refused with ${error ?? "invalid_client_metadata"}`, needs(registering, async () => {
      const r = await httpJson(base, "POST", "/register", { body: body() });
      expect([r.status, r.json?.error]).toEqual([400, error ?? "invalid_client_metadata"]);
      expect(Object.keys(r.json ?? {})).not.toContain("client_secret");
    }));
  }
});

describe("client authentication (RFC 7523)", () => {
  const refuses = (what: string, build: (rp: Awaited<ReturnType<typeof newRelyingParty>>["rp"], iss: string) => string) =>
    it(`${what} is 401`, needs(registering, async () => {
      const { rp } = await newRelyingParty(base);
      const r = await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: build(rp, await issuerOf(base)) });
      expect(r.status, JSON.stringify(r.json)).toBe(401);
    }));
  const b64 = (o: object) => Buffer.from(JSON.stringify(o)).toString("base64url");

  it("a good assertion works for ES256 and for PS256, and a replay of the same assertion is refused", needs(registering, async () => {
    for (const alg of ["ES256", "PS256"] as const) {
      const { rp } = await newRelyingParty(base, { alg });
      const jwt = clientAssertion(rp.key, rp.clientId, await issuerOf(base));
      expect((await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: jwt })).status, alg).toBe(201);
      expect((await rpCall(rp, "POST", "/consent/links", { body: {}, assertion: jwt })).status, alg + " replay").toBe(401);
    }
  }));
  refuses("a wrong audience", (rp) => clientAssertion(rp.key, rp.clientId, "https://evil.example"));
  refuses("an expired assertion", (rp, iss) => clientAssertion(rp.key, rp.clientId, iss, { now: nowS() - 120, exp: nowS() - 60 }));
  refuses("an assertion that lives more than 5 minutes", (rp, iss) => clientAssertion(rp.key, rp.clientId, iss, { exp: nowS() + 301 }));
  refuses("iss different from the client_id", (rp, iss) => clientAssertion(rp.key, rp.clientId, iss, { iss: "someone" }));
  refuses("sub different from the client_id", (rp, iss) => clientAssertion(rp.key, rp.clientId, iss, { sub: "someone" }));
  refuses("another key", (rp, iss) => clientAssertion(generateRpKey("ES256", rp.key.kid), rp.clientId, iss));
  refuses("alg none", (rp, iss) => `${b64({ alg: "none" })}.${b64({ iss: rp.clientId, sub: rp.clientId, aud: iss, exp: nowS() + 60, jti: "0123456789abcdef" })}.`);
  refuses("HS256 with the public key as the secret", (rp, iss) => {
    const h = `${b64({ alg: "HS256", kid: rp.key.kid })}.${b64({ iss: rp.clientId, sub: rp.clientId, aud: iss, exp: nowS() + 60, jti: "0123456789abcdef" })}`;
    return `${h}.${createHmac("sha256", JSON.stringify(rp.key.publicJwk)).update(h).digest("base64url")}`;
  });

  it("a client_id next to the assertion that is not the assertion's own is 401 (RFC 7523), in a JSON body and in a form", needs(registering, async () => {
    const { rp } = await newRelyingParty(base);
    const other = (await newRelyingParty(base)).rp;
    expect((await rpCall(rp, "POST", "/consent/links", { body: { client_id: rp.clientId } })).status, "its own client_id is fine").toBe(201);
    const json = await rpCall(rp, "POST", "/consent/links", { body: { client_id: other.clientId } });
    expect([json.status, json.json?.error]).toEqual([401, "invalid_client"]);
    const form = await rpCall(rp, "POST", "/token", { form: { grant_type: CIBA, auth_req_id: "ciba_x", client_id: other.clientId } });
    expect(form.status).toBe(401);
  }));

  it("such a client can never obtain sign, manage-limits or any wallet scope, and the signing endpoint takes nothing of it", needs(registering, async () => {
    const { rp } = await newRelyingParty(base);
    const g = buildGroup([usdcTransfer(config.address, account("conformance payee"), 5000)]);
    for (const scope of ["sign", "manage-limits", "enroll-device"]) {
      const r = await rpCall(rp, "POST", "/connect/ciba", { body: { scope, login_hint: config.loginHint, resource: "https://api.biatec.io", origin: config.origin, network: config.network, address: config.address, unsigned_group: g.items, limits_request: { per_transaction_eur: "1", per_day_eur: "1" } } });
      expect([400, 401, 403], scope + " " + JSON.stringify(r.json)).toContain(r.status);
    }
    const grant = await rpCall(rp, "POST", "/token", { form: { grant_type: "client_credentials", scope: "sign" } });
    expect(grant.status).toBeGreaterThanOrEqual(400);
    expect(grant.status).toBeLessThan(500);
    const sign = await rpCall(rp, "POST", `/wallet/${config.network}/${config.address}/sign`, { body: { transactions: g.items } });
    expect([401, 403]).toContain(sign.status);
  }));
});

describe("linking (spec 14.3)", () => {
  it("round trip: the answers match the schemas and the party verifies the user's statement with the pinned key and with the attestation", needs(playing, async () => {
    const p = await linkedParty();
    expect(schemaProblems("link-request", p.linkRequest)).toEqual([]);
    expect(p.linkRequest.client).toEqual({ fingerprint: clientFingerprint(p.rp.clientId), domain_proven: true, domain: p.host });
    expect(p.created.link_uri).toMatch(/^biatec-authenticator:\/\/link\?iss=[^&#\s]+&id=[A-Za-z0-9_-]{16,128}&s=[A-Za-z0-9_-]{43}$/);
    expect(new URL(p.created.link_uri).searchParams.get("iss")).toBe(await issuerOf(base));
    expect(p.created.expires_in).toBeLessThanOrEqual(300);
    expect(p.subject).toBeTruthy();
    expect(p.pins.length).toBeGreaterThanOrEqual(1);
    expect(p.pins.some((d) => d.key_use === "approve")).toBe(true);
    const expect_ = { link_id: p.created.link_id, client_fingerprint: clientFingerprint(p.rp.clientId), domain: p.host, nonce: p.linkRequest.nonce, binding_code: p.created.binding_code };
    const a = p.status.approval;
    const approval = { signed: a.signed, signature: a.signature, alg: "ES256" as const };
    expect(verifyLinkPinned(approval, p.pins, expect_, { consumed: new Set() }), "pinned mode").toBe(true);
    expect(verifyLinkPinned(approval, p.pins, expect_, { consumed: new Set([p.created.link_id]) }), "a link id already acted on").toBe(false);
    expect(verifyLinkPinned({ signed: a.signed, signature: a.signature } as never, p.pins, expect_, { consumed: new Set() }), "alg is required").toBe(false);
    expect(verifyLinkPinned(approval, p.pins, { ...expect_, domain: null }, { consumed: new Set() }), "the domain the user did not decide about").toBe(false);
    const jwks = (await http(base, "GET", "/.well-known/jwks.json")).json;
    const key = p.pins.find((d) => d.kid === a.signed.kid)!;
    const envelope = { signed: a.signed, alg: "ES256", signature: a.signature, device: { kid: key.kid, key_use: key.key_use, public_key: key.jwk, attestation: a.attestation } };
    expect(verifyLink(envelope, jwks, { issuer: await issuerOf(base), consumed: new Set() }, { ...expect_, sub: p.subject }), "attestation mode").toBe(true);
  }));

  it("without a proven domain the statement carries domain null", needs(playing, async () => {
    const p = await linkedParty({ prove: false });
    expect(p.linkRequest.client).toEqual({ fingerprint: clientFingerprint(p.rp.clientId), domain_proven: false });
    expect(p.status.approval.signed.domain).toBeNull();
  }));

  it("refusals: another domain, three wrong codes, a presence-key approve, a stale statement, a replay", needs(playing, async () => {
    const dev = await phone(base);
    const start = async () => {
      const { rp } = await newRelyingParty(base, { prove: true });
      const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
      return { rp, c };
    };
    let { rp, c } = await start();
    const domain = await approveLink(base, dev, c.link_uri, { code: c.binding_code, change: { domain: "evil.example" } });
    expect(domain.posted?.status, "wrong domain refused").toBe(400);
    const stale = await approveLink(base, dev, c.link_uri, { code: c.binding_code, now: nowS() - 301 });
    expect(stale.posted?.status, "issued_at more than 300 seconds off").toBe(400);
    expect((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status, "nothing above linked anyone").toBe("pending");
    const ok = await approveLink(base, dev, c.link_uri, { code: c.binding_code });
    expect(ok.posted?.status).toBe(200);
    const again = await approveLink(base, dev, c.link_uri, { code: c.binding_code });
    expect([again.linkRequest.status, again.posted?.status].some((s) => s === 409 || s === 404 || s === 410), "a link is single use").toBe(true);
    ({ rp, c } = await start());
    const w1 = await approveLink(base, dev, c.link_uri, { code: "000000" });
    expect([w1.posted?.status, w1.posted?.json.error]).toEqual([400, "wrong_code"]);
    await approveLink(base, dev, c.link_uri, { code: "000001" });
    const w3 = await approveLink(base, dev, c.link_uri, { code: "000002" });
    expect(w3.posted?.status).toBe(403);
    const closed = await approveLink(base, dev, c.link_uri, { code: c.binding_code });
    expect(closed.posted?.status ?? closed.linkRequest.status, "the closed link does not link").toBeGreaterThanOrEqual(400);
    expect(["expired", "denied"]).toContain((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status);
  }));

  it("the link statement needs the link secret like the fetch: missing or wrong is 404", needs(playing, async () => {
    const dev = await phone(base);
    const { rp } = await newRelyingParty(base, { prove: true });
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    const { id, secret } = parseLinkUri(c.link_uri);
    const lr = (await httpJson(base, "GET", `/consent/links/${id}`, { headers: { Authorization: dev.auth, "X-Link-Secret": secret } })).json;
    const signed = { type: "biatec_link", link_id: id, client_fingerprint: lr.client.fingerprint, domain: lr.client.domain ?? null, decision: "approve", binding_code: c.binding_code, nonce: lr.nonce, issued_at: nowS(), kid: dev.kid };
    const body = { signed, signature: signEs256(config.devicePrivateJwk!, Buffer.from(jcs(signed), "utf8")) };
    const post = (h: Record<string, string>) => httpJson(base, "POST", `/consent/links/${id}/receipt`, { headers: { Authorization: dev.auth, ...h }, body });
    expect((await post({})).status, "no secret").toBe(404);
    expect((await post({ "X-Link-Secret": "A".repeat(43) })).status, "wrong secret").toBe(404);
    expect((await rpCall(rp, "GET", `/consent/links/${id}`)).json.status, "nothing linked yet").toBe("pending");
    expect((await post({ "X-Link-Secret": secret })).status).toBe(200);
  }));

  it("a presence key never approves a link (it may deny)", needs([...playing, "presenceKey"], async () => {
    const dev = await phone(base);
    const { rp } = await newRelyingParty(base, { prove: true });
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    const r = await approveLink(base, dev, c.link_uri, { code: c.binding_code, presence: true });
    expect(r.posted?.status).toBe(400);
    expect((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json.status).toBe("pending");
    const deny = await approveLink(base, dev, c.link_uri, { code: "", decision: "deny", presence: true });
    expect(deny.posted?.status, JSON.stringify(deny.posted?.json)).toBe(200);
  }));

  it("a deny is a result: status denied with the signed approval, nothing linked", needs(playing, async () => {
    const dev = await phone(base);
    const { rp } = await newRelyingParty(base, { prove: true });
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    const d = await approveLink(base, dev, c.link_uri, { code: "", decision: "deny" });
    expect(d.posted?.status).toBe(200);
    const s = (await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).json;
    expect(schemaProblems("link-status", s)).toEqual([]);
    expect([s.status, s.approval.signed.decision, s.subject]).toEqual(["denied", "deny", undefined]);
  }));

  it("only the party that created a link reads it, and the device needs the secret", needs(playing, async () => {
    const dev = await phone(base);
    const { rp } = await newRelyingParty(base);
    const other = (await newRelyingParty(base)).rp;
    const c = (await rpCall(rp, "POST", "/consent/links", { body: {} })).json;
    expect((await rpCall(other, "GET", `/consent/links/${c.link_id}`)).status).toBe(404);
    const wrong = await httpJson(base, "GET", `/consent/links/${c.link_id}`, { headers: { Authorization: dev.auth, "X-Link-Secret": "A".repeat(43) } });
    expect(wrong.status).toBe(404);
    expect((await rpCall(rp, "GET", `/consent/links/${c.link_id}`)).status).toBe(200);
    expect(parseLinkUri(c.link_uri).id).toBe(c.link_id);
  }));

  it("a user_label that breaks the text rules is refused, not stripped", needs(registering, async () => {
    const { rp } = await newRelyingParty(base);
    for (const label of ["a‮b", "a​b", "line\nbreak", "x".repeat(41), " pad"]) {
      expect((await rpCall(rp, "POST", "/consent/links", { body: { user_label: label } })).status, JSON.stringify(label)).toBe(400);
    }
  }));
});

describe("requests (spec 14.4, 14.5)", () => {
  for (const [name, details] of [["payment", PAYMENT], ["login", LOGIN], ["action", ACTION]] as const) {
    it(`${name}: approved on the phone, redeemed, and the receipt verifies with the pinned key only`, needs(playing, async () => {
      const p = await linkedParty();
      const o = await open(p, details, { origin: ORIGIN });
      expect(o.status, JSON.stringify(o.json)).toBe(200);
      expect(schemaProblems("backchannel-authentication-response", o.json)).toEqual([]);
      const a = await approveRequest(base, p.dev, o.json.auth_req_id, { code: o.json.binding_code });
      expect(a.posted?.status, JSON.stringify(a.posted?.json)).toBe(200);
      expect(schemaProblems("consent-request", a.fetched.json)).toEqual([]);
      expect(a.fetched.json.sub).toBe(p.subject);
      expect(a.fetched.json.bound_hash).toBe(detailsHash({ ...details, origin: ORIGIN }));
      expect(JSON.stringify(a.fetched.json)).not.toContain(o.json.binding_code);
      const t = await redeem(p, o.json.auth_req_id);
      expect(t.status, JSON.stringify(t.json)).toBe(200);
      expect(schemaProblems("token-response", t.json)).toEqual([]);
      expect(t.json.scope).toBe("openid");
      const receipt = t.json.consent_receipt;
      expect(receipt.version).toBe("1.2");
      expect(receipt.signed.details_hash).toBe(detailsHash({ ...details, origin: ORIGIN }));
      const opts = { auth_req_id: o.json.auth_req_id, sub: p.subject, clientFingerprint: clientFingerprint(p.rp.clientId), bindingCode: o.json.binding_code, decision: "approve" as const, origin: ORIGIN, consumed: new Set<string>() };
      expect(verifyReceiptPinned(receipt, p.pins, details, opts), "pinned verification").toBe(true);
      expect(verifyReceiptPinned(receipt, p.pins, details, { ...opts, origin: undefined as never }), "the origin is explicit: absent is refused").toBe(false);
      expect(verifyReceiptPinned(receipt, p.pins, details, { ...opts, origin: null }), "none expected but one signed").toBe(false);
      expect(verifyReceiptPinned(receipt, p.pins, details, { ...opts, consumed: undefined as never }), "the party's consumed set is required").toBe(false);
      expect(verifyReceiptPinned(receipt, p.pins, details, { ...opts, consumed: new Set([opts.auth_req_id]) }), "replay: the id was recorded after the party acted").toBe(false);
      const jwks = (await http(base, "GET", "/.well-known/jwks.json")).json;
      expect(verifyReceipt(receipt, jwks, { issuer: await issuerOf(base) }), "attestation mode").toBe(true);
      // the same receipt at the party's retrieval paths (the existing one and the one of issue 38)
      const viaReceipts = await rpCall(p.rp, "GET", `/consent/receipts/${o.json.auth_req_id}`);
      const viaRequests = await rpCall(p.rp, "GET", `/consent/requests/${o.json.auth_req_id}/receipt`);
      expect([viaReceipts.status, viaRequests.status]).toEqual([200, 200]);
      expect(viaReceipts.json).toEqual(receipt);
      expect(viaRequests.json).toEqual(receipt);
      // one changed member of what the party sent
      const altered = structuredClone(details) as any;
      if (altered.creditor_account) altered.creditor_account.iban = "DE89370400440532013001";
      else if (altered.context) altered.context.ip = "203.0.113.43";
      else altered.lines[0].value = "Someone Else";
      expect(verifyReceiptPinned(receipt, p.pins, altered, opts)).toBe(false);
      // the redeemed token is useless at every other endpoint, and single use
      const bearer = { Authorization: "Bearer " + t.json.access_token };
      for (const [m, path] of [["POST", `/wallet/${config.network}/${config.address}/sign`], ["GET", "/consent/settings"], ["GET", "/devices"], ["PUT", "/wallet/limits"]] as const) {
        const r = await httpJson(base, m, path, { headers: bearer, body: m === "GET" ? undefined : { transactions: ["x"], per_transaction_eur: "1", per_day_eur: "1" } });
        expect([401, 403], `${m} ${path}`).toContain(r.status);
      }
      expect((await redeem(p, o.json.auth_req_id)).status).toBeGreaterThanOrEqual(400);
    }));
  }

  it("an unlinked party cannot open a request, whatever the content, for any subject: unknown_user_id", needs(playing, async () => {
    const p = await linkedParty();
    const { rp: stranger } = await newRelyingParty(base);
    const asks = [p.subject, "pw_0000000000000000", config.loginHint];
    for (const hint of asks) {
      for (const details of [LOGIN, { garbage: true }]) {
        const r = await rpCall(stranger, "POST", "/connect/ciba", { body: { scope: "openid", login_hint: hint, authorization_details: [details] } });
        expect([r.status, r.json?.error], `${hint}`).toEqual([400, "unknown_user_id"]);
      }
    }
  }));

  it("the party removes its own link: DELETE, then unknown_user_id; the devices endpoint lists the pinned keys while linked", needs(playing, async () => {
    const p = await linkedParty();
    const devices = await rpCall(p.rp, "GET", `/consent/links/subject/${p.subject}/devices`);
    expect(devices.status, JSON.stringify(devices.json)).toBe(200);
    expect(devices.json.devices.map((d: any) => d.kid).sort()).toEqual(p.pins.map((d) => d.kid).sort());
    expect(devices.json.devices.every((d: any) => !("d" in d.jwk))).toBe(true);
    expect((await rpCall(p.rp, "DELETE", `/consent/links/subject/${p.subject}`)).status).toBe(200);
    expect((await open(p, LOGIN)).json?.error).toBe("unknown_user_id");
    expect((await rpCall(p.rp, "GET", `/consent/links/subject/${p.subject}/devices`)).status).toBe(404);
  }));

  it("a deny and three wrong codes are results of their own: the receipt is fetched, no token is issued", needs(playing, async () => {
    const p = await linkedParty();
    const o = (await open(p, PAYMENT)).json;
    const deny = await approveRequest(base, p.dev, o.auth_req_id, { code: "", decision: "deny" });
    expect(deny.posted?.status, JSON.stringify(deny.posted?.json)).toBe(200);
    const r = (await rpCall(p.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).json;
    expect(verifyReceiptPinned(r, p.pins, PAYMENT, { origin: null, decision: "deny", auth_req_id: o.auth_req_id, sub: p.subject, clientFingerprint: clientFingerprint(p.rp.clientId), bindingCode: o.binding_code, consumed: new Set() })).toBe(true);
    expect(verifyReceiptPinned(r, p.pins, PAYMENT, { origin: null, decision: "approve", auth_req_id: o.auth_req_id, sub: p.subject, clientFingerprint: clientFingerprint(p.rp.clientId), bindingCode: o.binding_code, consumed: new Set() })).toBe(false);
    expect((await redeem(p, o.auth_req_id)).json.error).toBe("access_denied");
    const o2 = (await open(p, LOGIN)).json;
    const wrong = (code: string) => approveRequest(base, p.dev, o2.auth_req_id, { code });
    expect((await wrong("000001")).posted?.json.error).toBe("wrong_code");
    await wrong("000002");
    expect((await wrong("000003")).posted?.json.reason).toBe("code_failed");
    expect((await redeem(p, o2.auth_req_id)).json.error).toBe("access_denied");
  }));

  it("a request closed because the link was removed ends as access_denied with reason unlinked and has no receipt", needs(playing, async () => {
    const p = await linkedParty();
    const o = (await open(p, PAYMENT)).json;
    expect((await rpCall(p.rp, "DELETE", `/consent/links/subject/${p.subject}`)).status).toBe(200);
    const t = await redeem(p, o.auth_req_id);
    expect([t.status, t.json?.error, t.json?.reason]).toEqual([400, "access_denied", "unlinked"]);
    expect((await rpCall(p.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).status, "no receipt").toBe(404);
  }));

  it("a presence key never approves a request (it may deny)", needs([...playing, "presenceKey"], async () => {
    const p = await linkedParty();
    const o = (await open(p, PAYMENT)).json;
    const bad = await approveRequest(base, p.dev, o.auth_req_id, { code: o.binding_code, presence: true });
    expect([bad.posted?.status, bad.posted?.json.error]).toEqual([400, "invalid_receipt"]);
    const deny = await approveRequest(base, p.dev, o.auth_req_id, { code: "", decision: "deny", presence: true });
    expect(deny.posted?.status).toBe(200);
  }));

  it("the receipt must restate the request: another payment's details or a hash that is not theirs is refused", needs(playing, async () => {
    const p = await linkedParty();
    const o = (await open(p, PAYMENT)).json;
    const other = structuredClone(PAYMENT) as any;
    other.creditor_account.iban = "GB82WEST12345698765432";
    for (const change of [{ authorization_details: [other], details_hash: detailsHash(other) }, { details_hash: detailsHash(other) }, { sub: "pw_other" }, { binding_message: "Pay now" }]) {
      const r = await approveRequest(base, p.dev, o.auth_req_id, { code: o.binding_code, change });
      expect(r.posted?.status, JSON.stringify(change).slice(0, 60)).toBe(400);
    }
    const ok = await approveRequest(base, p.dev, o.auth_req_id, { code: o.binding_code });
    expect(ok.posted?.status, "the request stayed open").toBe(200);
  }));

  describe("what is refused with 400 invalid_authorization_details (nothing is stripped)", () => {
    const poison = ["a‮b", "a⁦b", "a‎b", "a​b", "a﻿b", "a\nb", "a\u0000b", "a\tb", " pad", "a‍b", "Café"];
    const pay = (patch: (d: any) => void) => { const d = structuredClone(PAYMENT) as any; patch(d); return d; };
    const cases: [string, unknown][] = [];
    for (const s of poison) {
      const e = JSON.stringify(s);
      cases.push([`creditor_name ${e}`, pay((d) => (d.creditor_name = s))], [`remittance ${e}`, pay((d) => (d.remittance = s))], [`identification ${e}`, pay((d) => (d.creditor_account = { identification: s, scheme: "BBAN" }))], [`scheme ${e}`, pay((d) => (d.creditor_account = { identification: "1", scheme: s }))]);
      cases.push([`login ip ${e}`, { type: "biatec_login", context: { ip: s } }], [`login user_agent ${e}`, { type: "biatec_login", context: { user_agent: s } }]);
      cases.push([`action title ${e}`, { ...ACTION, title: s }], [`action label ${e}`, { ...ACTION, lines: [{ label: s, value: "v" }] }], [`action value ${e}`, { ...ACTION, lines: [{ label: "l", value: s }] }]);
    }
    cases.push(["an IBAN with a wrong check digit", pay((d) => (d.creditor_account = { iban: "DE89370400440532013001" }))]);
    cases.push(["a lower-case IBAN", pay((d) => (d.creditor_account = { iban: "de89370400440532013000" }))]);
    for (const amount of [1250, "0", "0.00", "-1", "1.234", "1,50", "01.00", "1e3", ""]) cases.push([`amount ${JSON.stringify(amount)}`, pay((d) => (d.instructed_amount.amount = amount))]);
    cases.push(["a date that does not exist", pay((d) => (d.execution_date = "2026-02-30"))]);
    cases.push(["an unknown member", pay((d) => (d.priority = "high"))], ["origin inside the object", pay((d) => (d.origin = ORIGIN))], ["nine lines", { ...ACTION, lines: Array.from({ length: 9 }, () => ({ label: "l", value: "v" })) }]);
    cases.push(["a title over 60 characters", { ...ACTION, title: "x".repeat(61) }], ["a creditor_name over 70", pay((d) => (d.creditor_name = "x".repeat(71)))], ["a wallet type", { type: "biatec_limits_change" }]);
    it(`${cases.length} refusals`, needs(playing, async () => {
      const p = await linkedParty();
      const failures: string[] = [];
      for (const [label, details] of cases) {
        const r = await open(p, details);
        if (r.status !== 400 || r.json?.error !== "invalid_authorization_details") failures.push(`${label}: HTTP ${r.status} ${r.json?.error}`);
      }
      expect(failures).toEqual([]);
    }));
  });

  it("the threshold and first-use rules do not apply: even 0.01 EUR goes to the phone", needs(playing, async () => {
    const p = await linkedParty();
    const o = await open(p, { ...PAYMENT, instructed_amount: { amount: "0.01", currency: "EUR" } });
    expect(o.status, JSON.stringify(o.json)).toBe(200);
    expect(schemaProblems("backchannel-authentication-response", o.json)).toEqual([]);
  }));
});

describe("canonical form", () => {
  it("the receipt's signed object is in RFC 8785 form and the party's expected details hash to the signed hash", needs(playing, async () => {
    const p = await linkedParty();
    const o = (await open(p, LOGIN)).json;
    await approveRequest(base, p.dev, o.auth_req_id, { code: o.binding_code });
    const r = (await rpCall(p.rp, "GET", `/consent/receipts/${o.auth_req_id}`)).json;
    expect(jcs(JSON.parse(jcs(r.signed)))).toBe(jcs(r.signed));
    expect(r.signed.details_hash).toBe(detailsHash(r.signed.authorization_details[0]));
  }));
});
