import type { Algodv2, SuggestedParams } from "algosdk";
import type { AlgorandClient as AlgorandClientType } from "@algorandfoundation/algokit-utils";
import type { TransactionSignerAccount } from "@algorandfoundation/algokit-utils/types/account";
import type { BiatecNetworkConfig } from "biatec-concentrated-liquidity-amm";
import type { BuildContext } from "../app.js";
import { AlgorandClient, algosdk, clamm } from "./lib.js";

/** Everything a CLAMM builder needs for one network: algod, the Biatec app ids and an AlgoKit client. */
export interface ClammContext {
  algod: Algodv2;
  algorand: AlgorandClientType;
  apps: BiatecNetworkConfig;
  params: SuggestedParams;
}

/** The chain each network name must be served by (genesis id); a network without a Biatec deployment has no entry. */
const GENESIS: Record<string, string> = { mainnet: "mainnet-v1.0", testnet: "testnet-v1.0", voimain: "voimain-v1.0" };

export async function clammContext(ctx: BuildContext): Promise<ClammContext> {
  const expected = GENESIS[ctx.network];
  if (!expected) throw new Error(`no Biatec CLAMM deployment is known for network ${ctx.network}`);
  const algod = new algosdk.Algodv2("", ctx.algodUrl, "");
  const params = await algod.getTransactionParams().do();
  // The app ids follow from the chain itself (genesis id), and the chain must be the one the caller named, so a
  // misconfigured algod URL can't build a group for another chain than the one the user is shown.
  if (params.genesisID !== expected) throw new Error(`algod for ${ctx.network} serves ${params.genesisID || "an unknown chain"}`);
  const apps = clamm.getConfig(expected);
  return { algod, algorand: AlgorandClient.fromClients({ algod }), apps, params };
}

/** The account a group is built for. The sidecar never signs: the signer only satisfies the client types. */
export function unsignedAccount(address: string): TransactionSignerAccount {
  return { addr: algosdk.Address.fromString(address), signer: algosdk.makeEmptyTransactionSigner() };
}

export interface Identity {
  verificationClass: number;
  isLocked: boolean;
}

/**
 * The account's identity at the Biatec identity provider; an address without an identity is class 0. The lookup is a
 * simulated call paid by the account itself, so an account without ALGO for the fee is taken as class 0 (it couldn't
 * pay for any action either).
 */
export async function identityOf(c: ClammContext, address: string): Promise<Identity> {
  const { amount, minBalance } = await c.algod.accountInformation(address).exclude("all").do();
  if (amount < minBalance + 10_000n) return { verificationClass: 0, isLocked: false };
  const identity = new clamm.BiatecIdentityProviderClient({ algorand: c.algorand, appId: c.apps.identityAppId });
  // The ABI arg takes the address as a string, the composer's sender needs an Address.
  const result = await identity.send.getUserShort({
    args: { user: address, v: 1 },
    sender: algosdk.Address.fromString(address),
    signer: algosdk.makeEmptyTransactionSigner(),
  });
  return { verificationClass: Number(result.return?.verificationClass ?? 0n), isLocked: result.return?.isLocked ?? false };
}
