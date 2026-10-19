import { AlgorandClient } from "@algorandfoundation/algokit-utils";
import { AlgoAmount } from "@algorandfoundation/algokit-utils/types/amount";
import algosdk from "algosdk";
import {
  AlgoSafeFactory,
  buildAlgoSafeAppClient,
  fetchAlgoSafeSignerGroups,
  getAlgoSafeContractVersion,
  hasModernAbi,
  readHasApproved,
  readIsMember,
  readProposal,
  readSafeConfig,
  readTransactionGroup,
  resolveValidatorAppId,
  type AdminChange,
  type AlgoSafeConfigRecord,
  type AlgoSafeSignerGroupRecord,
  type GroupSeed,
  type Proposal,
  type SafeTxnTuple,
} from "algo-safe";

/** Everything the safe builders need from the chain. The real implementation wraps algo-safe; tests inject a fake. */
export interface SafeHandle {
  appId: bigint;
  appAddress: string;
  config(): Promise<AlgoSafeConfigRecord>;
  groups(): Promise<AlgoSafeSignerGroupRecord[]>;
  isMember(groupId: bigint, address: string): Promise<boolean>;
  proposal(proposalId: bigint): Promise<Proposal | undefined>;
  payload(proposalId: bigint, slot: bigint): Promise<SafeTxnTuple[] | undefined>;
  adminPayload(proposalId: bigint): Promise<AdminChange | undefined>;
  hasApproved(proposalId: bigint, address: string): Promise<boolean>;
  /** Fee in microAlgo for the outer call so it also covers its inner transactions. */
  proposeTxn(sender: string, args: { groupId: bigint; payload: SafeTxnTuple[]; expiryRound: bigint }): Promise<algosdk.Transaction>;
  proposeAdminTxn(sender: string, args: { groupId: bigint; change: AdminChange; expiryRound: bigint }): Promise<algosdk.Transaction>;
  approveTxn(sender: string, args: { proposalId: bigint; expectedPayloadVersion: bigint }): Promise<algosdk.Transaction>;
  executeTxn(sender: string, args: { proposalId: bigint; ensureBudgetValue: bigint; feeMicroAlgo: bigint }): Promise<algosdk.Transaction>;
  bootstrapTxns(sender: string, args: { groupName: string } | { groups: { seed: GroupSeed; members: [string, bigint, string][] }[] }): Promise<algosdk.Transaction[]>;
}

export interface Simulation {
  ok: boolean;
  failureMessage?: string;
  failedAt?: number[];
  appBudgetConsumed?: number;
}

export interface SafeChain {
  /** Throws when the app is not an algo-safe on the modern ABI. */
  safe(appId: bigint): Promise<SafeHandle>;
  resolveValidatorAppId(explicit?: bigint): Promise<bigint>;
  currentRound(): Promise<bigint>;
  deployTxn(sender: string, name: string, validatorAppId: bigint): Promise<algosdk.Transaction>;
  /** Dry-run the group against algod without signatures. Failure is a result, not an exception. */
  simulate(txns: algosdk.Transaction[]): Promise<Simulation>;
  /** Add the account, app, asset and box references the group needs, found by simulating it. Returns the same transactions. */
  populateResources(txns: algosdk.Transaction[]): Promise<algosdk.Transaction[]>;
}

export function createAlgodChain(algodUrl: string, token = ""): SafeChain {
  const algod = new algosdk.Algodv2(token, algodUrl);
  const algorand = AlgorandClient.fromClients({ algod });

  async function safe(appId: bigint): Promise<SafeHandle> {
    const version = await getAlgoSafeContractVersion(algod, appId);
    if (!hasModernAbi(version)) throw new Error("unsupported_safe_version: this safe runs an older contract that cannot be driven");
    const appAddress = algosdk.getApplicationAddress(appId).toString();
    const client = await buildAlgoSafeAppClient(algod, { appId, address: appAddress });
    const ref = { appId, address: appAddress };
    return {
      appId,
      appAddress,
      config: () => readSafeConfig(client),
      groups: () => fetchAlgoSafeSignerGroups(algod, ref),
      isMember: (groupId, address) => readIsMember(client, groupId, address),
      proposal: (id) => readProposal(client, id),
      payload: async (id, slot) => (await readTransactionGroup(client, id, slot)) as SafeTxnTuple[] | undefined,
      adminPayload: async (id) => {
        try {
          return await client.state.box.adminPayloads.value(id);
        } catch (e) {
          if (e instanceof Error && (e.message.includes("box not found") || e.message.includes("404"))) return undefined;
          throw e;
        }
      },
      hasApproved: (id, address) => readHasApproved(client, id, address),
      proposeTxn: async (sender, a) =>
        one(
          await client.createTransaction.proposeTransactionGroup({
            sender,
            args: { groupId: a.groupId, payload: a.payload, expiryRound: a.expiryRound, execute: false, ensureBudgetValue: 0n },
          }),
        ),
      proposeAdminTxn: async (sender, a) =>
        one(
          await client.createTransaction.proposeAdminChange({
            sender,
            args: { groupId: a.groupId, change: a.change, expiryRound: a.expiryRound, ensureBudgetValue: 0n },
          }),
        ),
      approveTxn: async (sender, a) =>
        one(
          await client.createTransaction.approveProposal({
            sender,
            args: { proposalId: a.proposalId, expectedPayloadVersion: a.expectedPayloadVersion, ensureBudgetValue: 0n },
          }),
        ),
      executeTxn: async (sender, a) =>
        one(
          await client.createTransaction.executeProposal({
            sender,
            args: { proposalId: a.proposalId, ensureBudgetValue: a.ensureBudgetValue },
            staticFee: AlgoAmount.MicroAlgo(a.feeMicroAlgo),
          }),
        ),
      bootstrapTxns: async (sender, a) => {
        if ("groupName" in a) return [one(await client.createTransaction.bootstrap({ sender, args: { groupName: a.groupName } }))];
        const txns: algosdk.Transaction[] = [];
        for (const g of a.groups) {
          txns.push(one(await client.createTransaction.bootstrapGroup({ sender, args: { seed: g.seed, members: g.members, ensureBudgetValue: 0n } })));
        }
        txns.push(one(await client.createTransaction.finalizeBootstrap({ sender, args: [] })));
        return txns;
      },
    };
  }

  return {
    safe,
    currentRound: async () => BigInt((await algod.status().do()).lastRound),
    resolveValidatorAppId: (explicit) => resolveValidatorAppId(algod, explicit === undefined ? {} : { appId: explicit }),
    deployTxn: async (sender, name, validatorAppId) => {
      const factory = algorand.client.getTypedAppFactory(AlgoSafeFactory, { defaultSender: sender });
      const { transactions } = await factory.createTransaction.create.createApplication({ args: { name, validatorAppId } });
      const txn = transactions[0];
      if (!txn) throw new Error("create produced no transaction");
      return txn;
    },
    simulate: async (txns) => {
      const group = (await simulateGroup(txns)).txnGroups[0];
      const failure = group?.failureMessage;
      return {
        ok: !failure,
        ...(failure ? { failureMessage: failure } : {}),
        ...(group?.failedAt ? { failedAt: [...group.failedAt].map(Number) } : {}),
        ...(group?.appBudgetConsumed !== undefined ? { appBudgetConsumed: Number(group.appBudgetConsumed) } : {}),
      };
    },
    populateResources: async (txns) => {
      // Only calls to existing apps can need references; a deployment has none.
      if (!txns.some((t) => t.applicationCall && t.applicationCall.appIndex !== 0n)) return txns;
      const group = (await simulateGroup(txns)).txnGroups[0];
      // A failing simulation names no resources; the caller sees the failure when it asks for simulate.
      if (!group || group.failureMessage) return txns;
      group.txnResults.forEach((result, i) => {
        const call = txns[i]?.applicationCall;
        if (call) addAccessed(call, result.unnamedResourcesAccessed);
      });
      // Group-level resources are shared: give them to the first app call.
      const first = txns.find((t) => t.applicationCall)?.applicationCall;
      if (first) addAccessed(first, group.unnamedResourcesAccessed);
      return txns;
    },
  };

  async function simulateGroup(txns: algosdk.Transaction[]) {
    const grouped = txns.length > 1 ? algosdk.assignGroupID(txns) : txns;
    const request = new algosdk.modelsv2.SimulateRequest({
      txnGroups: [new algosdk.modelsv2.SimulateRequestTransactionGroup({ txns: grouped.map((txn) => new algosdk.SignedTransaction({ txn })) })],
      allowEmptySignatures: true,
      allowUnnamedResources: true,
    });
    return algod.simulateTransactions(request).do();
  }
}

type Accessed = algosdk.modelsv2.SimulateUnnamedResourcesAccessed | undefined;

/** Copy the references algod says the call touched into the call itself, so it also succeeds without allowUnnamedResources. */
function addAccessed(readonlyCall: NonNullable<algosdk.Transaction["applicationCall"]>, a: Accessed): void {
  if (!a) return;
  // The SDK types these as readonly but they are plain arrays on the object, and the encoder reads them when the transaction is encoded.
  const call = readonlyCall as unknown as { accounts: algosdk.Address[]; foreignApps: bigint[]; foreignAssets: bigint[]; boxes: { appIndex: bigint; name: Uint8Array }[] };
  const accounts = [...(a.accounts ?? []), ...(a.appLocals ?? []).map((l) => l.account), ...(a.assetHoldings ?? []).map((h) => h.account)];
  for (const acct of accounts) if (!call.accounts.some((x) => x.toString() === acct.toString())) call.accounts.push(acct);
  for (const app of a.apps ?? []) if (!call.foreignApps.includes(app)) call.foreignApps.push(app);
  const assets = [...(a.assets ?? []), ...(a.assetHoldings ?? []).map((h) => h.asset)];
  for (const asset of assets) if (!call.foreignAssets.includes(asset)) call.foreignAssets.push(asset);
  for (const box of a.boxes ?? []) {
    if (!call.boxes.some((x) => x.appIndex === box.app && Buffer.from(x.name).equals(Buffer.from(box.name)))) call.boxes.push({ appIndex: box.app, name: box.name });
  }
}

function one(r: { transactions: algosdk.Transaction[] }): algosdk.Transaction {
  const t = r.transactions[0];
  if (!t) throw new Error("algo-safe produced no transaction");
  return t;
}
