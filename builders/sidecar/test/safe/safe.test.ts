import algosdk from "algosdk";
import { ACT_ALL, ACT_PAY, PRIV_GROUP, PRIV_POLICY, PT_ADMIN, PT_TRANSACTION_GROUP, ZERO_ADDR, createAssetSafeTxn, createPaymentSafeTxn, toSafeTxnGroup } from "algo-safe";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { createApp, type BuildContext } from "../../src/app.js";
import { loadConfig } from "../../src/config.js";
import { createAlgodChain, type SafeChain } from "../../src/safe/chain.js";
import { safeBuilders, executeBudget } from "../../src/safe/builders.js";
import { FakeAlgod, boxName, encodeStruct } from "./fake-algod.js";

const SECRET = "test-secret-0123456789";
const alice = algosdk.generateAccount().addr.toString();
const bob = algosdk.generateAccount().addr.toString();
const carol = algosdk.generateAccount().addr.toString();
const receiver = algosdk.generateAccount().addr.toString();

const APP = 777;
const BOOT_APP = 778;
const fake = new FakeAlgod();

const paymentGroup = toSafeTxnGroup([createPaymentSafeTxn({ sender: ZERO_ADDR, receiver, amount: 2_500_000n, hasClose: 0n, closeRemainderTo: ZERO_ADDR, note: "rent" })]);

function put(appId: number, name: Uint8Array, value: Uint8Array) {
  fake.setBox(appId, name, value);
}

function signerGroup(over: Partial<Record<string, bigint | string>> = {}) {
  const v = { name: "Admins", threshold: 2n, memberCount: 2n, adminPrivileges: PRIV_GROUP | PRIV_POLICY, allowedActions: ACT_ALL, active: 1n, ...over };
  return encodeStruct("SignerGroup", [v.name as string, v.threshold as bigint, v.memberCount as bigint, v.adminPrivileges as bigint, v.allowedActions as bigint, 0n, 0n, 0n, 0n, 0n, 0n, 0n, 0n, 0n, 1n, v.active as bigint, 0n, 0n]);
}

function proposal(over: Partial<{ status: bigint; payloadType: bigint; approvals: bigint; version: bigint; numPayloads: bigint; proposer: string; expiry: bigint }> = {}) {
  const v = { status: 1n, payloadType: PT_TRANSACTION_GROUP, approvals: 1n, version: 3n, numPayloads: 1n, proposer: alice, expiry: 9_999_999n, ...over };
  return encodeStruct("Proposal", [1n, v.status, v.payloadType, v.approvals, 2n, v.expiry, v.proposer, v.numPayloads, 1n, v.version, 1n]);
}

const txGroupType = algosdk.ABIType.from("(uint64,byte[])[]");

beforeAll(async () => {
  await fake.start();
  fake.addSafe({ appId: APP, globals: { name: "Treasury", nextGroupId: 2n, nextProposalId: 5n, groupCount: 1n }, boxes: new Map() });
  put(APP, boxName("groups", "uint64", 1n), signerGroup());
  for (const addr of [alice, bob]) put(APP, boxName("members", "(uint64,address)", [1n, addr]), encodeStruct("Member", [0n, "", addr]));
  // 1: active, alice proposed and approved. 2: ready. 3: admin change. 4: executed.
  put(APP, boxName("proposals", "uint64", 1n), proposal());
  put(APP, boxName("transactionGroups", "uint64", 1n * 7n + 1n), txGroupType.encode(paymentGroup));
  put(APP, boxName("approvals", "(uint64,address)", [1n, alice]), encodeStruct("Approval", [alice, 10n]));
  put(APP, boxName("proposals", "uint64", 2n), proposal({ status: 2n, approvals: 2n }).slice());
  put(APP, boxName("transactionGroups", "uint64", 2n * 7n + 1n), txGroupType.encode(paymentGroup));
  put(APP, boxName("proposals", "uint64", 3n), proposal({ payloadType: PT_ADMIN, version: 1n, numPayloads: 0n }));
  put(APP, boxName("adminPayloads", "uint64", 3n), encodeStruct("AdminChange", [4n, 1n, "", ZERO_ADDR, 0n, "", 3n, 0n, 0n, 0n, 0n, 0n, 0n, 0n, 0n]));
  put(APP, boxName("proposals", "uint64", 4n), proposal({ status: 3n }));
  fake.addSafe({ appId: BOOT_APP, globals: { name: "Fresh", nextGroupId: 1n, nextProposalId: 1n, groupCount: 0n }, boxes: new Map() });
});
afterAll(() => fake.stop());

const ctx = (): BuildContext => ({ network: "testnet", algodUrl: fake.url });
/** Real chain against the fake algod, except the validator lookup which needs the validator's bytecode on chain. */
const chainFactory = (c: BuildContext): SafeChain => ({ ...createAlgodChain(c.algodUrl), resolveValidatorAppId: async (x) => x ?? 4242n });
const b = () => safeBuilders(chainFactory);

const decode = (b64: string) => algosdk.decodeUnsignedTransaction(Buffer.from(b64, "base64"));
const uintArg = (t: algosdk.Transaction, i: number) => algosdk.bytesToBigInt(t.applicationCall!.appArgs![i]!);

describe("proposal.create", () => {
  it("builds an unsigned propose call with a decoded payment preview", async () => {
    const r = await b()["proposal.create"]!({ appId: APP, sender: alice, groupId: 1, payload: [{ type: "payment", receiver, amount: "2500000", note: "rent" }] }, ctx());
    expect(r.transactions).toHaveLength(1);
    const t = decode(r.transactions[0]!);
    expect(t.type).toBe("appl");
    expect(t.applicationCall!.appIndex).toBe(BigInt(APP));
    expect(t.sender.toString()).toBe(alice);
    expect(t.group).toBeUndefined();
    const preview = r.preview as { payload: Record<string, unknown>[]; valueMicroAlgo: string; kind: string };
    expect(preview.kind).toBe("safe.propose");
    expect(preview.valueMicroAlgo).toBe("0");
    expect(preview.payload).toEqual([{ type: "payment", from: "safe", receiver, amount: "2500000", closesAccountTo: null, note: "rent" }]);
  });

  it("round-trips a payment payload through the read helper", async () => {
    const r = await b()["proposal.create"]!({ appId: APP, sender: bob, groupId: 1, payload: [{ type: "payment", receiver, amount: "2500000", note: "rent" }] }, ctx());
    // What the chain would store: the payload argument of the built call, written to the proposal's payload box.
    const t = decode(r.transactions[0]!);
    const stored = t.applicationCall!.appArgs![2]!;
    put(APP, boxName("proposals", "uint64", 20n), proposal());
    put(APP, boxName("transactionGroups", "uint64", 20n * 7n + 1n), stored);
    const read = await b()["safe.proposals"]!({ appId: APP, member: bob, limit: 100 }, { ...ctx() });
    const p = (read.data as { proposals: { proposalId: string; payload: unknown }[] }).proposals;
    // proposal 20 is above nextProposalId (5); bump the safe's counter by reading with a fresh app
    expect(p.find((x) => x.proposalId === "20")).toBeUndefined();
    fake.safes.get(APP)!.globals.nextProposalId = 21n;
    const again = await b()["safe.proposals"]!({ appId: APP, member: bob, limit: 100 }, ctx());
    const found = (again.data as { proposals: { proposalId: string; payload: unknown }[] }).proposals.find((x) => x.proposalId === "20")!;
    expect(found.payload).toEqual((r.preview as { payload: unknown }).payload);
    fake.safes.get(APP)!.globals.nextProposalId = 5n;
  });

  it("builds an admin change proposal", async () => {
    const r = await b()["proposal.create"]!({ appId: APP, sender: alice, groupId: 1, change: { type: "changeThreshold", groupId: 1, threshold: 1 } }, ctx());
    expect(decode(r.transactions[0]!).applicationCall!.appIndex).toBe(BigInt(APP));
    expect((r.preview as { change: { type: string; threshold: string } }).change).toMatchObject({ type: "changeThreshold", threshold: "1" });
  });

  it("refuses createGroup with several members or a threshold above the member count, and past expiry", async () => {
    const run = (params: unknown) => b()["proposal.create"]!(params, ctx());
    const m = (a: string) => ({ address: a });
    const base = { appId: APP, sender: alice, groupId: 1 };
    await expect(run({ ...base, change: { type: "createGroup", name: "Ops", threshold: 2, members: [m(alice), m(bob)] } })).rejects.toThrow(/exactly one initial member/);
    await expect(run({ ...base, change: { type: "createGroup", name: "Ops", threshold: 2, members: [m(alice)] } })).rejects.toThrow(/threshold/);
    await expect(run({ ...base, change: { type: "createGroup", name: "Ops", members: [] } })).rejects.toThrow(/exactly one initial member/);
    const ok = await run({ ...base, change: { type: "createGroup", name: "Ops", threshold: 1, members: [m(alice)] } });
    expect(ok.transactions).toHaveLength(1);
    await expect(run({ ...base, expiryRound: 1000, payload: [{ type: "payment", receiver, amount: 1 }] })).rejects.toThrow(/future/);
  });

  it("refuses non-members, bad input and actions the group may not take", async () => {
    const run = (params: unknown) => b()["proposal.create"]!(params, ctx());
    const pay = [{ type: "payment", receiver, amount: 1 }];
    await expect(run({ appId: APP, sender: carol, groupId: 1, payload: pay })).rejects.toThrow(/not a member/);
    await expect(run({ appId: APP, sender: alice, groupId: 1, payload: [{ type: "payment", receiver: "nope", amount: 1 }] })).rejects.toThrow(/address/);
    await expect(run({ appId: APP, sender: alice, groupId: 1, payload: [{ type: "payment", receiver, amount: -1 }] })).rejects.toThrow(/integer/);
    await expect(run({ appId: APP, sender: alice, groupId: 1 })).rejects.toThrow(/exactly one/);
    await expect(run({ appId: APP, sender: alice, groupId: 1, payload: pay, change: {} })).rejects.toThrow(/exactly one/);
    await expect(run({ appId: APP, sender: alice, groupId: 1, payload: [] })).rejects.toThrow(/1 to 16/);
    put(APP, boxName("groups", "uint64", 2n), signerGroup({ allowedActions: ACT_PAY }));
    put(APP, boxName("members", "(uint64,address)", [2n, alice]), encodeStruct("Member", [0n, "", alice]));
    fake.safes.get(APP)!.globals.nextGroupId = 3n;
    await expect(run({ appId: APP, sender: alice, groupId: 2, payload: [{ type: "assetTransfer", assetId: 31566704, receiver, amount: 5 }] })).rejects.toThrow(/not allowed/);
    const ok = await run({ appId: APP, sender: alice, groupId: 2, payload: pay });
    expect(ok.transactions).toHaveLength(1);
  });
});

describe("proposal.approve", () => {
  it("includes the expectedPayloadVersion the member reviewed", async () => {
    const r = await b()["proposal.approve"]!({ appId: APP, sender: bob, proposalId: 1, expectedPayloadVersion: 3 }, ctx());
    const t = decode(r.transactions[0]!);
    expect(uintArg(t, 1)).toBe(1n); // proposalId
    expect(uintArg(t, 2)).toBe(3n); // expectedPayloadVersion
    const preview = r.preview as { expectedPayloadVersion: string; approvalsAfter: string; threshold: string; proposal: { payload: unknown[] } };
    expect(preview.expectedPayloadVersion).toBe("3");
    expect(preview.approvalsAfter).toBe("2");
    expect(preview.threshold).toBe("2");
    expect(preview.proposal.payload).toHaveLength(1);
  });

  it("requires the reviewed expectedPayloadVersion", async () => {
    await expect(b()["proposal.approve"]!({ appId: APP, sender: bob, proposalId: 1 }, ctx())).rejects.toThrow(/expectedPayloadVersion is required/);
  });

  it("refuses an expired proposal", async () => {
    put(APP, boxName("proposals", "uint64", 6n), proposal({ expiry: 500n }));
    await expect(b()["proposal.approve"]!({ appId: APP, sender: bob, proposalId: 6, expectedPayloadVersion: 3 }, ctx())).rejects.toThrow(/expired/);
  });

  it("admin proposals approve at version 1", async () => {
    const r = await b()["proposal.approve"]!({ appId: APP, sender: bob, proposalId: 3, expectedPayloadVersion: 1 }, ctx());
    expect(uintArg(decode(r.transactions[0]!), 2)).toBe(1n);
  });

  it("refuses a stale version, a repeat approval, a non-member and a closed proposal", async () => {
    const run = (params: unknown) => b()["proposal.approve"]!(params, ctx());
    await expect(run({ appId: APP, sender: bob, proposalId: 1, expectedPayloadVersion: 2 })).rejects.toThrow(/payload changed/);
    await expect(run({ appId: APP, sender: alice, proposalId: 1, expectedPayloadVersion: 3 })).rejects.toThrow(/already approved/);
    await expect(run({ appId: APP, sender: carol, proposalId: 1, expectedPayloadVersion: 3 })).rejects.toThrow(/not a member/);
    await expect(run({ appId: APP, sender: bob, proposalId: 4, expectedPayloadVersion: 3 })).rejects.toThrow(/not active/);
    await expect(run({ appId: APP, sender: bob, proposalId: 99, expectedPayloadVersion: 1 })).rejects.toThrow(/not found/);
  });
});

describe("proposal.execute", () => {
  it("sizes budget and fee from the payload", async () => {
    const r = await b()["proposal.execute"]!({ appId: APP, sender: carol, proposalId: 2 }, ctx());
    const t = decode(r.transactions[0]!);
    // one payment: 3400 + 700 + 800
    expect(uintArg(t, 2)).toBe(4900n);
    // 1 outer + 1 payload + 1 validator call + 7 opups
    expect(t.fee).toBe(10_000n);
    expect((r.preview as { ensureBudgetValue: string }).ensureBudgetValue).toBe("4900");
  });

  it("needs no budget for an admin change that is ready", async () => {
    put(APP, boxName("proposals", "uint64", 3n), proposal({ status: 2n, payloadType: PT_ADMIN, version: 1n, numPayloads: 0n }));
    const r = await b()["proposal.execute"]!({ appId: APP, sender: carol, proposalId: 3 }, ctx());
    expect(uintArg(decode(r.transactions[0]!), 2)).toBe(0n);
  });

  it("refuses a proposal that is not ready", async () => {
    await expect(b()["proposal.execute"]!({ appId: APP, sender: carol, proposalId: 1 }, ctx())).rejects.toThrow(/not ready/);
  });

  it("budget formula covers each transaction type", () => {
    const t = toSafeTxnGroup([
      createPaymentSafeTxn({ sender: ZERO_ADDR, receiver, amount: 1n, hasClose: 0n, closeRemainderTo: ZERO_ADDR, note: "" }),
      createAssetSafeTxn({ sender: ZERO_ADDR, xferAsset: 5n, assetReceiver: receiver, assetAmount: 1n, hasClose: 0n, assetCloseTo: ZERO_ADDR, note: "" }),
    ]);
    expect(executeBudget(t)).toBe(3400n + 2n * 700n + 2n * 800n);
  });
});

describe("safe.list and safe.proposals", () => {
  it("lists safes the address is a member of", async () => {
    const r = await b()["safe.list"]!({ member: bob, appIds: [APP, BOOT_APP, 31337] }, ctx());
    const data = r.data as { safes: { appId: string; name: string; groups: { id: string }[] }[]; skipped: { appId: string }[] };
    expect(data.safes.map((s) => [s.appId, s.name, s.groups.map((g) => g.id)])).toEqual([[String(APP), "Treasury", ["1"]]]);
    expect(data.skipped.map((s) => s.appId)).toEqual(["31337"]);
    expect(r.transactions).toEqual([]);
    const none = await b()["safe.list"]!({ member: carol, appIds: [APP] }, ctx());
    expect((none.data as { safes: unknown[] }).safes).toEqual([]);
  });

  it("returns proposals newest first with progress and previews, filtered by status", async () => {
    const r = await b()["safe.proposals"]!({ appId: APP, member: alice }, ctx());
    const ps = (r.data as { proposals: { proposalId: string; status: string; approvals: string; threshold: string; approvedByMember: boolean; payload: unknown[] }[] }).proposals;
    expect(ps.map((p) => p.proposalId)).toEqual(["4", "3", "2", "1"]);
    const one = ps.find((p) => p.proposalId === "1")!;
    expect(one).toMatchObject({ status: "active", approvals: "1", threshold: "2", approvedByMember: true });
    expect(one.payload).toEqual([{ type: "payment", from: "safe", receiver, amount: "2500000", closesAccountTo: null, note: "rent" }]);
    const ready = await b()["safe.proposals"]!({ appId: APP, status: "ready" }, ctx());
    expect((ready.data as { proposals: { proposalId: string }[] }).proposals.map((p) => p.proposalId)).toEqual(["3", "2"]);
    await expect(b()["safe.proposals"]!({ appId: APP, status: "bogus" }, ctx())).rejects.toThrow(/status/);
  });
});

describe("safe.proposals scan bound", () => {
  it("stops after a bounded scan and returns a cursor", async () => {
    fake.addSafe({ appId: 790, globals: { name: "Big", nextGroupId: 1n, nextProposalId: 1301n, groupCount: 0n }, boxes: new Map() });
    const first = await b()["safe.proposals"]!({ appId: 790, status: "ready" }, ctx());
    expect(first.data).toMatchObject({ proposals: [], truncated: true, before: 801n });
    const last = await b()["safe.proposals"]!({ appId: 790, status: "ready", before: 301 }, ctx());
    expect(last.data).toMatchObject({ truncated: false });
  }, 60_000);
});

describe("safe.create", () => {
  it("builds the deployment call and nothing that moves funds", async () => {
    const r = await b()["safe.create"]!({ sender: alice, name: "Team Safe" }, ctx());
    expect(r.transactions).toHaveLength(1);
    const t = decode(r.transactions[0]!);
    expect(t.type).toBe("appl");
    expect(t.applicationCall!.appIndex).toBe(0n);
    expect(t.sender.toString()).toBe(alice);
    expect(r.preview).toMatchObject({ kind: "safe.create", step: "deploy", name: "Team Safe", valueMicroAlgo: "0", validatorAppId: "4242" });
  });

  it("builds bootstrap for a fresh safe, and a grouped bootstrap with finalize", async () => {
    const single = await b()["safe.create"]!({ sender: alice, step: "bootstrap", appId: BOOT_APP }, ctx());
    expect(single.transactions).toHaveLength(1);

    const multi = await b()["safe.create"]!(
      { sender: alice, step: "bootstrap", appId: BOOT_APP, groups: [{ name: "Admins", threshold: 2, members: [{ address: alice }, { address: bob }] }, { name: "Spenders", members: [{ address: carol }], allowedActions: 3 }] },
      ctx(),
    );
    expect(multi.transactions).toHaveLength(3);
    const txns = multi.transactions.map(decode);
    const gid = txns[0]!.group;
    expect(gid).toBeDefined();
    expect(txns.every((t) => t.group && Buffer.from(t.group).equals(Buffer.from(gid!)))).toBe(true);
    expect(txns.every((t) => t.applicationCall!.appIndex === BigInt(BOOT_APP))).toBe(true);
    expect((multi.preview as { groups: { name: string; threshold: string }[] }).groups.map((g) => [g.name, g.threshold])).toEqual([["Admins", "2"], ["Spenders", "1"]]);
  });

  it("refuses bootstrap of a safe that already has groups, and bad groups", async () => {
    const run = (params: unknown) => b()["safe.create"]!(params, ctx());
    await expect(run({ sender: alice, step: "bootstrap", appId: APP })).rejects.toThrow(/already bootstrapped/);
    await expect(run({ sender: alice, step: "bootstrap", appId: BOOT_APP, groups: [{ name: "A", threshold: 3, members: [{ address: alice }] }] })).rejects.toThrow(/threshold/);
    await expect(run({ sender: alice, step: "bootstrap", appId: BOOT_APP, groups: [{ name: "A", members: [{ address: alice }, { address: alice }] }] })).rejects.toThrow(/duplicate/);
    await expect(run({ sender: alice, step: "nope" })).rejects.toThrow(/step/);
    await expect(run({ sender: "bad", name: "x" })).rejects.toThrow(/address/);
  });
});

describe("simulation and resources", () => {
  it("reports a simulation of the create-safe group when asked", async () => {
    const r = await b()["safe.create"]!({ sender: alice, step: "bootstrap", appId: BOOT_APP, simulate: true, groups: [{ name: "Admins", members: [{ address: alice }] }] }, ctx());
    expect(r.simulation).toEqual({ ok: true, appBudgetConsumed: 1234 });
    // the group was simulated as one group of two calls
    expect(fake.simulated.at(-1)).toHaveLength(2);
    fake.failure = "logic eval error: assert failed";
    try {
      const bad = await b()["safe.create"]!({ sender: alice, step: "bootstrap", appId: BOOT_APP, simulate: true }, ctx());
      expect(bad.simulation).toMatchObject({ ok: false, failureMessage: "logic eval error: assert failed" });
      expect(bad.transactions).toHaveLength(1);
    } finally {
      fake.failure = undefined;
    }
  });

  it("does not simulate unless asked, except to find references", async () => {
    const before = fake.simulated.length;
    const r = await b()["proposal.approve"]!({ appId: APP, sender: bob, proposalId: 1, expectedPayloadVersion: 3 }, ctx());
    expect(r.simulation).toBeUndefined();
    expect(fake.simulated.length).toBe(before + 1);
    const deploy = await b()["safe.create"]!({ sender: alice, name: "Team Safe" }, ctx());
    expect(deploy.simulation).toBeUndefined();
    expect(fake.simulated.length).toBe(before + 1);
  });

  it("copies the boxes the simulation touched into the call", async () => {
    const box = boxName("proposals", "uint64", 2n);
    fake.accessed = () => new algosdk.modelsv2.SimulateUnnamedResourcesAccessed({ boxes: [new algosdk.modelsv2.BoxReference({ app: BigInt(APP), name: box })], accounts: [algosdk.Address.fromString(receiver)] });
    try {
      const r = await b()["proposal.execute"]!({ appId: APP, sender: carol, proposalId: 2 }, ctx());
      const call = decode(r.transactions[0]!).applicationCall!;
      expect(call.boxes.map((x) => Buffer.from(x.name).toString("base64"))).toContain(Buffer.from(box).toString("base64"));
      expect(call.accounts.map((a) => a.toString())).toContain(receiver);
    } finally {
      fake.accessed = () => undefined;
    }
  });
});
describe("over HTTP", () => {
  const app = createApp(loadConfig({ BUILDERS_INTERNAL_SECRET: SECRET, ALGOD_URL_TESTNET: "http://unused" }), b());
  const post = (path: string, body: unknown) =>
    app.request(path, { method: "POST", headers: { "x-internal-secret": SECRET, "content-type": "application/json" }, body: JSON.stringify(body) });

  it("serialises bigints and passes the preview through", async () => {
    // route the request to the fake algod through the per-request context
    const routed = createApp(loadConfig({ BUILDERS_INTERNAL_SECRET: SECRET, ALGOD_URL_TESTNET: fake.url }), b());
    const r = await routed.request("/v1/build/proposal.approve", {
      method: "POST",
      headers: { "x-internal-secret": SECRET, "content-type": "application/json" },
      body: JSON.stringify({ network: "testnet", params: { appId: APP, sender: bob, proposalId: 1, expectedPayloadVersion: "3" } }),
    });
    expect(r.status).toBe(200);
    const body = (await r.json()) as { transactions: string[]; preview: { expectedPayloadVersion: string } };
    expect(body.transactions).toHaveLength(1);
    expect(body.preview.expectedPayloadVersion).toBe("3");
  });

  it("maps a refused build to 422 with the reason", async () => {
    const routed = createApp(loadConfig({ BUILDERS_INTERNAL_SECRET: SECRET, ALGOD_URL_TESTNET: fake.url }), b());
    const r = await routed.request("/v1/build/proposal.approve", {
      method: "POST",
      headers: { "x-internal-secret": SECRET, "content-type": "application/json" },
      body: JSON.stringify({ network: "testnet", params: { appId: APP, sender: bob, proposalId: 1, expectedPayloadVersion: 1 } }),
    });
    expect(r.status).toBe(422);
    expect(((await r.json()) as { message: string }).message).toMatch(/payload changed/);
    expect((await post("/v1/build/safe.list", { network: "testnet", params: {} })).status).toBe(422);
  });
});

/**
 * Opt-in: runs the real simulate path (allowEmptySignatures, allowUnnamedResources, group id) against a live node.
 * TESTNET_ALGOD_URL=<testnet algod> TESTNET_SAFE_SENDER=<funded testnet address> pnpm test
 */
describe.skipIf(!process.env.TESTNET_ALGOD_URL || !process.env.TESTNET_SAFE_SENDER)("safe.create simulate against testnet", () => {
  it("returns a simulation outcome from a real algod", async () => {
    const real = safeBuilders();
    const r = await real["safe.create"]!({ sender: process.env.TESTNET_SAFE_SENDER, name: "Sim Safe", simulate: true }, { network: "testnet", algodUrl: process.env.TESTNET_ALGOD_URL! });
    expect(r.transactions).toHaveLength(1);
    expect(r.simulation).toMatchObject({ ok: expect.any(Boolean) });
  }, 60_000);
});
