import { createHash } from "node:crypto";
import algosdk from "algosdk";
import {
  ACT_ALL,
  ACT_AXFER,
  ACT_PAY,
  ADM_ADD_MEMBER,
  ADM_CHANGE_THRESHOLD,
  ADM_CREATE_GROUP,
  ADM_REMOVE_MEMBER,
  ADM_SET_ACTIVE,
  GT_STANDARD,
  PRIV_GROUP,
  PRIV_POLICY,
  PT_ADMIN,
  TX_ACFG,
  TX_APP,
  TX_KEYREG,
  TX_ASSET,
  TX_PAYMENT,
  ZERO_ADDR,
  algosdkTxnsToSafeTxnGroup,
  createAdminChange,
  createAppCallSafeTxn,
  createAssetSafeTxn,
  createPaymentSafeTxn,
  decodeAppTxn,
  decodeAssetTxn,
  decodePaymentTxn,
  toSafeTxnGroup,
  type AdminChange,
  type GroupSeed,
  type Proposal,
  type SafeTxn,
  type SafeTxnTuple,
} from "algo-safe";
import type { BuildContext, BuildResult, Builder } from "../app.js";
import { addLiquidityForProposal } from "../clamm/builders.js";
import { createAlgodChain, type SafeChain, type SafeHandle } from "./chain.js";
import { BadRequest, actionBitForTxType, actionNames, address, describeAdminChange, describePayload, obj, text, toJson, uint, type Json } from "./codec.js";

export const STATUS_ACTIVE = 1n;
export const STATUS_READY = 2n;
const STATUS_NAMES = new Map<bigint, string>([
  [1n, "active"],
  [2n, "ready"],
  [3n, "executed"],
  [4n, "cancelled"],
]);

const MIN_FEE = 1000n;
const MAX_LIST = 50;
const MAX_PAGE = 100;
/** Most proposal ids one safe.proposals request will probe; the caller continues with `before`. */
const MAX_SCAN = 500n;
/** About a week of rounds at roughly 2.8 seconds each. */
const DEFAULT_EXPIRY_ROUNDS = 216_000n;
const MAX_PAYLOAD_TXNS = 16;
/**
 * Extra fee per app-call payload entry, pooled by the outer executeProposal call, for the inner transactions the
 * called app sends itself (a Biatec DEX addLiquidity call pays 7 of its own, as its 8000 microAlgo static fee shows).
 */
const APP_CALL_INNER_FEE_ALLOWANCE = 7n * MIN_FEE;

const PAYLOAD_CODEC = algosdk.ABIType.from("(uint64,byte[])[]");
const ADMIN_CODEC = algosdk.ABIType.from("(uint64,uint64,string,address,uint64,string,uint64,uint64,uint64,uint64,uint64,uint64,uint64,uint64,uint64)");

/**
 * SHA-256 (hex) of the payload exactly as the propose call encodes it and the safe stores it: the transaction group
 * as `(uint64,byte[])[]`, an admin change as its tuple. The backend recomputes it from the propose call's own bytes,
 * so a landed proposal is found by what it contains, never by a description.
 */
export function payloadHash(payload: SafeTxnTuple[] | AdminChange): string {
  const bytes = Array.isArray(payload)
    ? PAYLOAD_CODEC.encode(payload)
    : ADMIN_CODEC.encode([
        payload.changeType, payload.targetGroupId, payload.groupName, payload.memberAddr, payload.memberType, payload.memberLabel,
        payload.threshold, payload.adminPrivileges, payload.allowedActions, payload.limitAssetId, payload.dailyLimit,
        payload.monthlyLimit, payload.cooldownRounds, payload.activeFlag, payload.guardAmount,
      ]);
  return createHash("sha256").update(bytes).digest("hex");
}

export type ChainFactory = (ctx: BuildContext) => SafeChain;

/** Builds a CLAMM add-liquidity group for the given sender (the safe's account), as encoded unsigned transactions. */
export type LiquidityGroupBuilder = (params: unknown, ctx: BuildContext) => Promise<{ transactions: Uint8Array[]; facts: Record<string, unknown> }>;

/** Clears any grp a simulation left behind: assignGroupID hashes each transaction as it is, so a stale grp would change the new group id. */
export function regroup(txns: algosdk.Transaction[]): algosdk.Transaction[] {
  for (const t of txns) t.group = undefined;
  return txns.length > 1 ? algosdk.assignGroupID(txns) : txns;
}

function encode(txns: algosdk.Transaction[]): string[] {
  const grouped = regroup(txns);
  return grouped.map((t) => Buffer.from(algosdk.encodeUnsignedTransaction(t)).toString("base64"));
}

function summarise(txns: algosdk.Transaction[]): Json[] {
  return txns.map((t) => {
    const call = t.applicationCall;
    return toJson({
      type: t.type,
      sender: t.sender.toString(),
      fee: BigInt(t.fee),
      appId: call ? call.appIndex : undefined,
      firstValid: t.firstValid,
      lastValid: t.lastValid,
    });
  });
}

async function finish(
  chain: SafeChain,
  txns: algosdk.Transaction[],
  preview: Record<string, unknown>,
  simulate: boolean,
  data?: unknown,
): Promise<BuildResult> {
  const transactions = encode(await chain.populateResources(txns));
  const result: BuildResult = { transactions, preview: toJson({ ...preview, transactions: summarise(txns) }) };
  if (data !== undefined) result.data = toJson(data);
  if (simulate) result.simulation = toJson(await chain.simulate(txns));
  return result;
}

const wantsSimulate = (p: Record<string, unknown>): boolean => p.simulate === true;

// ---- safe/create ----

function parseGroups(raw: unknown): { seed: GroupSeed; members: [string, bigint, string][] }[] {
  if (!Array.isArray(raw) || raw.length === 0 || raw.length > 8) throw new BadRequest("groups must have 1 to 8 entries");
  return raw.map((g, i) => {
    const o = obj(g, `groups[${i}]`);
    const membersRaw = o.members;
    if (!Array.isArray(membersRaw) || membersRaw.length === 0 || membersRaw.length > 15) throw new BadRequest(`groups[${i}].members must have 1 to 15 entries`);
    const members = membersRaw.map((m, j): [string, bigint, string] => {
      const mo = obj(m, `groups[${i}].members[${j}]`);
      return [address(mo.address, `groups[${i}].members[${j}].address`), 0n, text(mo.label, "member label", 64, { optional: true })];
    });
    if (new Set(members.map((m) => m[0])).size !== members.length) throw new BadRequest(`groups[${i}] has a duplicate member`);
    const threshold = uint(o.threshold ?? members.length, `groups[${i}].threshold`, { min: 1n, max: BigInt(members.length) });
    const isFirst = i === 0;
    const allowedActions = o.allowedActions === undefined ? ACT_PAY | ACT_AXFER : uint(o.allowedActions, `groups[${i}].allowedActions`, { max: ACT_ALL });
    const adminPrivileges = o.adminPrivileges === undefined ? (isFirst ? PRIV_GROUP | PRIV_POLICY : 0n) : uint(o.adminPrivileges, `groups[${i}].adminPrivileges`, { max: 7n });
    return {
      seed: {
        name: text(o.name, `groups[${i}].name`, 32),
        threshold,
        adminPrivileges,
        allowedActions,
        limitAssetId: uint(o.limitAssetId ?? 0, "limitAssetId"),
        dailyLimit: uint(o.dailyLimit ?? 0, "dailyLimit"),
        monthlyLimit: uint(o.monthlyLimit ?? 0, "monthlyLimit"),
        cooldownRounds: uint(o.cooldownRounds ?? 0, "cooldownRounds"),
        groupType: GT_STANDARD,
      },
      members,
    };
  });
}

/**
 * Two steps because the app id only exists once the deployment has landed.
 * `deploy` returns the single create call. `bootstrap` (needs `appId`) returns bootstrap + finalize.
 * Funding the safe's account for box storage is a separate wallet transfer, never part of these groups.
 */
export function safeCreate(makeChain: ChainFactory): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const sender = address(p.sender, "sender");
    const step = p.step ?? "deploy";
    if (step === "deploy") {
      const name = text(p.name, "name", 64);
      const validatorAppId = await chain.resolveValidatorAppId(p.validatorAppId === undefined ? undefined : uint(p.validatorAppId, "validatorAppId", { min: 1n }));
      const txn = await chain.deployTxn(sender, name, validatorAppId);
      return finish(chain, [txn], { kind: "safe.create", step: "deploy", intent: `Deploy Biatec Safe "${name}"`, name, validatorAppId, network: ctx.network, valueMicroAlgo: 0n,
        notes: ["The safe's app id is known once this lands. Fund its account with a separate transfer, then run the bootstrap step."] }, wantsSimulate(p));
    }
    if (step === "bootstrap") {
      const appId = uint(p.appId, "appId", { min: 1n });
      const safe = await chain.safe(appId);
      const cfg = await safe.config();
      if (cfg.groupCount !== 0n) throw new BadRequest("safe is already bootstrapped");
      const groups = p.groups === undefined ? undefined : parseGroups(p.groups);
      const txns = groups ? await safe.bootstrapTxns(sender, { groups }) : await safe.bootstrapTxns(sender, { groupName: text(p.groupName ?? "Admins", "groupName", 32) });
      const described = groups
        ? groups.map((g) => ({ name: g.seed.name, threshold: g.seed.threshold, members: g.members.map((m) => m[0]), allowedActions: actionNames(g.seed.allowedActions), adminPrivileges: g.seed.adminPrivileges }))
        : [{ name: p.groupName ?? "Admins", threshold: 1n, members: [sender], allowedActions: actionNames(ACT_ALL), adminPrivileges: PRIV_GROUP | PRIV_POLICY }];
      return finish(chain, txns, { kind: "safe.create", step: "bootstrap", intent: `Create the signer groups of safe ${appId}`, appId, safeAddress: safe.appAddress, groups: described, network: ctx.network, valueMicroAlgo: 0n,
        notes: ["The safe's account must hold enough ALGO for box storage before this group is submitted."] }, wantsSimulate(p));
    }
    throw new BadRequest("step must be deploy or bootstrap");
  };
}

// ---- reading ----

async function proposalView(safe: SafeHandle, id: bigint, p: Proposal, member?: string): Promise<Json> {
  let payload: Json = [];
  let hash: string | null = null;
  if (p.payloadType === PT_ADMIN) {
    const change = await safe.adminPayload(id);
    payload = change ? [describeAdminChange(change)] : [];
    hash = change ? payloadHash(change) : null;
  } else {
    const all: SafeTxnTuple[] = [];
    for (let slot = 1n; slot <= p.numPayloads; slot++) {
      const part = await safe.payload(id, slot);
      if (part) all.push(...part);
    }
    payload = describePayload(all);
    hash = payloadHash(all);
  }
  const view: Record<string, unknown> = {
    proposalId: id,
    groupId: p.groupId,
    kind: p.payloadType === PT_ADMIN ? "admin" : "transactionGroup",
    status: STATUS_NAMES.get(p.status) ?? String(p.status),
    approvals: p.approvalsCount,
    threshold: p.threshold,
    proposer: p.proposer,
    expiryRound: p.expiryRound,
    payloadVersion: p.payloadVersion,
    payloadHash: hash,
    payload,
  };
  if (member) view.approvedByMember = await safe.hasApproved(id, member);
  return toJson(view);
}

export function safeList(makeChain: ChainFactory): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const member = address(p.member, "member");
    const ids = p.appIds;
    if (!Array.isArray(ids) || ids.length === 0 || ids.length > MAX_LIST) throw new BadRequest(`appIds must have 1 to ${MAX_LIST} entries`);
    const safes: Json[] = [];
    const skipped: Json[] = [];
    for (const raw of ids) {
      const appId = uint(raw, "appIds[]", { min: 1n });
      try {
        const safe = await chain.safe(appId);
        const cfg = await safe.config();
        const groups = await safe.groups();
        const mine: Record<string, unknown>[] = [];
        for (const g of groups) {
          if (g.active && (await safe.isMember(BigInt(g.id), member))) {
            mine.push({ id: g.id, name: g.name, threshold: g.threshold, memberCount: g.memberCount, allowedActions: actionNames(BigInt(g.allowedActions)), isAdminGroup: g.isAdminGroup, membershipEpoch: g.membershipEpoch });
          }
        }
        if (mine.length > 0) safes.push(toJson({ appId, address: safe.appAddress, name: cfg.name, paused: cfg.paused === 1n, groups: mine }));
      } catch (e) {
        skipped.push(toJson({ appId, reason: e instanceof Error ? e.message.split("\n")[0] : "unreadable" }));
      }
    }
    return { transactions: [], data: { member, safes, skipped } };
  };
}

export function safeProposals(makeChain: ChainFactory): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const safe = await chain.safe(uint(p.appId, "appId", { min: 1n }));
    const member = p.member === undefined ? undefined : address(p.member, "member");
    const limit = Number(uint(p.limit ?? 25, "limit", { min: 1n, max: BigInt(MAX_PAGE) }));
    const want = p.status === undefined ? undefined : [...STATUS_NAMES].find(([, n]) => n === p.status)?.[0];
    if (p.status !== undefined && want === undefined) throw new BadRequest("status must be active, ready, executed or cancelled");
    const cfg = await safe.config();
    const out: Json[] = [];
    // `before` is the exclusive upper id of the page, so a cursor from a truncated reply continues where it stopped.
    const top = p.before === undefined ? cfg.nextProposalId : uint(p.before, "before", { min: 1n });
    let id = (top < cfg.nextProposalId ? top : cfg.nextProposalId) - 1n;
    let scanned = 0n;
    // Newest first. Pruned proposals have no box and are skipped. The scan is bounded: a filter few proposals match must not cost thousands of RPCs.
    for (; id >= 1n && out.length < limit && scanned < MAX_SCAN; id--, scanned++) {
      const prop = await safe.proposal(id);
      if (!prop || (want !== undefined && prop.status !== want)) continue;
      out.push(await proposalView(safe, id, prop, member));
    }
    const truncated = id >= 1n;
    return { transactions: [], data: { appId: safe.appId, safeAddress: safe.appAddress, proposals: out, truncated, ...(truncated ? { before: id + 1n } : {}) } };
  };
}

// ---- proposal/create ----

function payloadEntry(raw: unknown, i: number): SafeTxn {
  const o = obj(raw, `payload[${i}]`);
  const note = text(o.note, "note", 1024, { optional: true });
  switch (o.type) {
    case "payment":
      // sender is always the safe itself: the zero address.
      return createPaymentSafeTxn({ sender: ZERO_ADDR, receiver: address(o.receiver, "receiver"), amount: uint(o.amount, "amount"), hasClose: 0n, closeRemainderTo: ZERO_ADDR, note });
    case "assetTransfer":
      return createAssetSafeTxn({ sender: ZERO_ADDR, xferAsset: uint(o.assetId, "assetId", { min: 1n }), assetReceiver: address(o.receiver, "receiver"), assetAmount: uint(o.amount, "amount"), hasClose: 0n, assetCloseTo: ZERO_ADDR, note });
    case "appCall": {
      const args = o.appArgs ?? [];
      if (!Array.isArray(args) || args.length > 16) throw new BadRequest("appArgs must have at most 16 entries");
      const accounts = (o.accounts ?? []) as unknown[];
      if (!Array.isArray(accounts) || accounts.length > 4) throw new BadRequest("accounts must have at most 4 entries");
      return createAppCallSafeTxn({
        appId: uint(o.appId, "appId", { min: 1n }),
        onCompletion: uint(o.onCompletion ?? 0, "onCompletion", { max: 5n }),
        appArgs: args.map((a, k) => Buffer.from(text(a, `appArgs[${k}]`, 2048, { optional: true }), "base64")),
        accounts: accounts.map((a) => address(a, "accounts[]")),
        foreignApps: ((o.foreignApps ?? []) as unknown[]).map((a) => uint(a, "foreignApps[]")),
        foreignAssets: ((o.foreignAssets ?? []) as unknown[]).map((a) => uint(a, "foreignAssets[]")),
        note,
      });
    }
    default:
      throw new BadRequest(`payload[${i}].type must be payment, assetTransfer or appCall`);
  }
}

function adminChange(raw: unknown): AdminChange {
  const o = obj(raw, "change");
  switch (o.type) {
    case "createGroup": {
      // The contract takes a single initial member; more members are added with addMember proposals afterwards.
      if (!Array.isArray(o.members) || o.members.length !== 1) throw new BadRequest("createGroup takes exactly one initial member; add the others with addMember proposals");
      const first = obj(o.members[0], "members[0]");
      return createAdminChange({
        changeType: ADM_CREATE_GROUP,
        groupName: text(o.name, "name", 32),
        threshold: uint(o.threshold ?? 1, "threshold", { min: 1n, max: 1n }),
        memberAddr: address(first.address, "members[0].address"),
        memberLabel: text(first.label, "label", 64, { optional: true }),
        allowedActions: uint(o.allowedActions ?? ACT_PAY, "allowedActions", { max: ACT_ALL }),
        adminPrivileges: uint(o.adminPrivileges ?? 0, "adminPrivileges", { max: 7n }),
      });
    }
    case "addMember":
      return createAdminChange({ changeType: ADM_ADD_MEMBER, targetGroupId: uint(o.groupId, "groupId", { min: 1n }), memberAddr: address(o.member, "member"), memberLabel: text(o.label, "label", 64, { optional: true }) });
    case "removeMember":
      return createAdminChange({ changeType: ADM_REMOVE_MEMBER, targetGroupId: uint(o.groupId, "groupId", { min: 1n }), memberAddr: address(o.member, "member") });
    case "changeThreshold":
      return createAdminChange({ changeType: ADM_CHANGE_THRESHOLD, targetGroupId: uint(o.groupId, "groupId", { min: 1n }), threshold: uint(o.threshold, "threshold", { min: 1n }) });
    case "setActive":
      return createAdminChange({ changeType: ADM_SET_ACTIVE, targetGroupId: uint(o.groupId, "groupId", { min: 1n }), activeFlag: o.active === true ? 1n : 0n });
    default:
      throw new BadRequest("change.type must be createGroup, addMember, removeMember, changeThreshold or setActive");
  }
}

/**
 * A Biatec DEX add-liquidity payload for the safe: the group `biatec-concentrated-liquidity-amm` builds for the safe's
 * application account (with the LP-token opt-in first when the safe is not opted in yet), converted by algo-safe.
 * Payments and asset transfers sent by the safe's own account are stored with the zero address, which means the safe.
 */
async function liquidityPayload(build: LiquidityGroupBuilder, safe: SafeHandle, raw: unknown, ctx: BuildContext): Promise<{ tuples: SafeTxnTuple[]; facts: Record<string, unknown> }> {
  const o = obj(raw, "liquidity");
  const params = {
    sender: safe.appAddress,
    poolAppId: uint(o.poolAppId, "liquidity.poolAppId", { min: 1n }).toString(),
    amountA: uint(o.amountA, "liquidity.amountA").toString(),
    amountB: uint(o.amountB, "liquidity.amountB").toString(),
  };
  const { transactions, facts } = await build(params, ctx);
  if (transactions.length === 0 || transactions.length > MAX_PAYLOAD_TXNS) throw new BadRequest(`the liquidity group must have 1 to ${MAX_PAYLOAD_TXNS} transactions`);
  // Decoded with this module's algosdk: the CLAMM library runs on its own copy.
  const txns = transactions.map((b) => algosdk.decodeUnsignedTransaction(b));
  for (const t of txns) {
    if (t.sender.toString() !== safe.appAddress) throw new BadRequest("the liquidity group has a transaction the safe does not send");
    t.group = undefined;
  }
  const tuples = algosdkTxnsToSafeTxnGroup(txns).map(([txType, data]): SafeTxnTuple => {
    if (txType === TX_PAYMENT) {
      const d = decodePaymentTxn(data);
      return d.sender === safe.appAddress ? [txType, createPaymentSafeTxn({ ...d, sender: ZERO_ADDR }).data] : [txType, data];
    }
    if (txType === TX_ASSET) {
      const d = decodeAssetTxn(data);
      return d.sender === safe.appAddress ? [txType, createAssetSafeTxn({ ...d, sender: ZERO_ADDR }).data] : [txType, data];
    }
    return [txType, data];
  });
  return { tuples, facts };
}

/**
 * Builds the single proposal call. The proposal itself moves nothing; a payload's funding is a separate wallet transfer.
 * `payload` (transaction group) or `change` (admin change) selects the proposal kind.
 */
export function proposalCreate(makeChain: ChainFactory, liquidity: LiquidityGroupBuilder = addLiquidityForProposal): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const sender = address(p.sender, "sender");
    const safe = await chain.safe(uint(p.appId, "appId", { min: 1n }));
    const groupId = uint(p.groupId, "groupId", { min: 1n });
    if (!(await safe.isMember(groupId, sender))) throw new BadRequest("sender is not a member of that signer group");
    const expiryRound = p.expiryRound === undefined ? (await chain.currentRound()) + DEFAULT_EXPIRY_ROUNDS : uint(p.expiryRound, "expiryRound", { min: 1n });
    if (expiryRound <= (await chain.currentRound())) throw new BadRequest("expiryRound must be in the future");

    const sources = [p.payload, p.change, p.liquidity].filter((x) => x !== undefined).length;
    if (sources !== 1) throw new BadRequest("give exactly one of payload, change or liquidity");

    if (p.payload !== undefined || p.liquidity !== undefined) {
      let tuples: SafeTxnTuple[];
      let liquidityFacts: Record<string, unknown> | undefined;
      if (p.liquidity !== undefined) {
        ({ tuples, facts: liquidityFacts } = await liquidityPayload(liquidity, safe, p.liquidity, ctx));
      } else {
        if (!Array.isArray(p.payload) || p.payload.length === 0 || p.payload.length > MAX_PAYLOAD_TXNS) throw new BadRequest(`payload must have 1 to ${MAX_PAYLOAD_TXNS} entries`);
        tuples = toSafeTxnGroup(p.payload.map(payloadEntry));
      }
      const group = (await safe.groups()).find((g) => g.id === groupId.toString());
      if (!group || !group.active) throw new BadRequest("signer group does not exist or is inactive");
      const allowed = BigInt(group.allowedActions);
      for (const [t] of tuples) {
        if ((allowed & actionBitForTxType(t)) === 0n) throw new BadRequest("the signer group is not allowed to propose that kind of transaction");
      }
      const txn = await safe.proposeTxn(sender, { groupId, payload: tuples, expiryRound });
      return finish(chain, [txn], { kind: "safe.propose", intent: `Propose a transaction group on safe ${safe.appId}`, appId: safe.appId, safeAddress: safe.appAddress, groupId, expiryRound, network: ctx.network, valueMicroAlgo: 0n,
        payload: describePayload(tuples), payloadHash: payloadHash(tuples), ...(liquidityFacts ? { liquidity: liquidityFacts } : {}),
        notes: ["Proposing moves nothing. The proposer's own approval is recorded with the proposal; other members approve, then anyone executes once the threshold is met."] }, wantsSimulate(p));
    }

    const change = adminChange(p.change);
    const txn = await safe.proposeAdminTxn(sender, { groupId, change, expiryRound });
    return finish(chain, [txn], { kind: "safe.propose", intent: `Propose an administrative change on safe ${safe.appId}`, appId: safe.appId, safeAddress: safe.appAddress, groupId, expiryRound, network: ctx.network, valueMicroAlgo: 0n,
      change: describeAdminChange(change), payloadHash: payloadHash(change) }, wantsSimulate(p));
  };
}

// ---- proposal/approve and execute ----

async function loadProposal(safe: SafeHandle, p: Record<string, unknown>): Promise<{ id: bigint; prop: Proposal }> {
  const id = uint(p.proposalId, "proposalId", { min: 1n });
  const prop = await safe.proposal(id);
  if (!prop) throw new BadRequest("proposal not found");
  return { id, prop };
}

/**
 * The approval is bound to the payload version the member reviewed. The caller must supply `expectedPayloadVersion`, and it must match
 * the live one, otherwise the proposal changed since the preview and the build is refused.
 */
export function proposalApprove(makeChain: ChainFactory): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const sender = address(p.sender, "sender");
    const safe = await chain.safe(uint(p.appId, "appId", { min: 1n }));
    const { id, prop } = await loadProposal(safe, p);
    if (prop.status !== STATUS_ACTIVE) throw new BadRequest(`proposal is ${STATUS_NAMES.get(prop.status) ?? "not open"}, not active`);
    if (!(await safe.isMember(prop.groupId, sender))) throw new BadRequest("sender is not a member of the proposal's signer group");
    if (await safe.hasApproved(id, sender)) throw new BadRequest("sender has already approved this proposal");
    if (p.expectedPayloadVersion === undefined) throw new BadRequest("expectedPayloadVersion is required: approve the version you reviewed");
    const expectedPayloadVersion = uint(p.expectedPayloadVersion, "expectedPayloadVersion", { min: 1n });
    if (prop.expiryRound <= (await chain.currentRound())) throw new BadRequest("proposal has expired");
    if (expectedPayloadVersion !== prop.payloadVersion) throw new BadRequest("payload changed since review; review the proposal again");
    const txn = await safe.approveTxn(sender, { proposalId: id, expectedPayloadVersion });
    const view = await proposalView(safe, id, prop, sender);
    return finish(chain, [txn], { kind: "safe.approve", intent: `Approve proposal ${id} on safe ${safe.appId}`, appId: safe.appId, safeAddress: safe.appAddress, proposalId: id, expectedPayloadVersion,
      approvalsAfter: prop.approvalsCount + 1n, threshold: prop.threshold, network: ctx.network, proposal: view }, wantsSimulate(p));
  };
}

/** Opcode budget for executeProposal, from the algo-safe README sizing formula, rounded up. */
export function executeBudget(tuples: SafeTxnTuple[]): bigint {
  let budget = 3400n + BigInt(tuples.length) * 700n;
  for (const [txType, data] of tuples) {
    if (txType === TX_KEYREG) budget += 1000n;
    else if (txType === TX_ACFG) budget += 1500n;
    else if (txType === TX_APP) {
      const a = decodeAppTxn(data);
      budget += 1500n + 400n * BigInt(a.appArgs.length) + 200n * BigInt(a.accounts.length + a.foreignApps.length + a.foreignAssets.length);
    } else budget += 800n;
  }
  return budget;
}

export function proposalExecute(makeChain: ChainFactory): Builder {
  return async (params, ctx) => {
    const p = obj(params, "params");
    const chain = makeChain(ctx);
    const sender = address(p.sender, "sender");
    const safe = await chain.safe(uint(p.appId, "appId", { min: 1n }));
    const { id, prop } = await loadProposal(safe, p);
    if (prop.status !== STATUS_READY) throw new BadRequest(`proposal is ${STATUS_NAMES.get(prop.status) ?? "unknown"}, not ready to execute`);
    const tuples: SafeTxnTuple[] = [];
    if (prop.payloadType !== PT_ADMIN) {
      for (let slot = 1n; slot <= prop.numPayloads; slot++) tuples.push(...((await safe.payload(id, slot)) ?? []));
    }
    const ensureBudgetValue = prop.payloadType === PT_ADMIN ? 0n : executeBudget(tuples);
    // Inner transactions: one per payload entry, one validator call per non-app-call entry, and the opcode top-ups.
    const validatorCalls = BigInt(tuples.filter(([t]) => t !== TX_APP).length);
    const opups = (ensureBudgetValue + 699n) / 700n;
    const appCalls = BigInt(tuples.filter(([t]) => t === TX_APP).length);
    const feeMicroAlgo = MIN_FEE * (1n + BigInt(tuples.length) + validatorCalls + opups) + APP_CALL_INNER_FEE_ALLOWANCE * appCalls;
    const txn = await safe.executeTxn(sender, { proposalId: id, ensureBudgetValue, feeMicroAlgo });
    const view = await proposalView(safe, id, prop);
    return finish(chain, [txn], { kind: "safe.execute", intent: `Execute proposal ${id} on safe ${safe.appId}`, appId: safe.appId, safeAddress: safe.appAddress, proposalId: id, ensureBudgetValue, feeMicroAlgo,
      network: ctx.network, proposal: view }, wantsSimulate(p));
  };
}

/** The builders this module registers, keyed by the name used in POST /v1/build/:builder. */
export function safeBuilders(makeChain: ChainFactory = (ctx) => createAlgodChain(ctx.algodUrl), liquidity: LiquidityGroupBuilder = addLiquidityForProposal): Record<string, Builder> {
  return {
    "safe.create": safeCreate(makeChain),
    "safe.list": safeList(makeChain),
    "safe.proposals": safeProposals(makeChain),
    "proposal.create": proposalCreate(makeChain, liquidity),
    "proposal.approve": proposalApprove(makeChain),
    "proposal.execute": proposalExecute(makeChain),
  };
}
