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

export async function clammContext(ctx: BuildContext): Promise<ClammContext> {
  const algod = new algosdk.Algodv2("", ctx.algodUrl, "");
  const params = await algod.getTransactionParams().do();
  // The app ids follow from the chain itself (genesis id), so a misnamed network can't pick another chain's apps.
  const apps = clamm.getConfig(params.genesisID ?? "");
  return { algod, algorand: AlgorandClient.fromClients({ algod }), apps, params };
}

/** The account a group is built for. The sidecar never signs: the signer only satisfies the client types. */
export function unsignedAccount(address: string): TransactionSignerAccount {
  return { addr: algosdk.Address.fromString(address), signer: algosdk.makeEmptyTransactionSigner() };
}

/** Verification class of an account in the Biatec identity provider; an address without an identity is class 0. */
export async function verificationClassOf(c: ClammContext, address: string): Promise<number> {
  const identity = new clamm.BiatecIdentityProviderClient({ algorand: c.algorand, appId: c.apps.identityAppId });
  // The ABI arg takes the address as a string, the composer's sender needs an Address.
  const result = await identity.send.getUserShort({
    args: { user: address, v: 1 },
    sender: algosdk.Address.fromString(address),
    signer: algosdk.makeEmptyTransactionSigner(),
  });
  return Number(result.return?.verificationClass ?? 0n);
}
