import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import algosdk from "algosdk";
import { APP_SPEC } from "algo-safe";

/**
 * A tiny algod stand-in on 127.0.0.1 so the real algo-safe client and readers run end to end without a network.
 * It serves suggested params, status, application lookup (with the real approval program, so version detection works)
 * and boxes. Anything else is a 501 so a test fails loudly instead of hanging.
 */
export interface FakeSafe {
  appId: number;
  globals: { name: string; nextGroupId: bigint; nextProposalId: bigint; groupCount: bigint };
  boxes: Map<string, Uint8Array>;
}

const GENESIS_HASH = Buffer.alloc(32, 7).toString("base64");

const b64 = (bytes: Uint8Array) => Buffer.from(bytes).toString("base64");
const u64 = (n: bigint) => algosdk.bigIntToBytes(n, 8);

/** Box name as algo-safe's typed client computes it: the map prefix followed by the ABI-encoded key. */
export function boxName(map: keyof typeof APP_SPEC.state.maps.box | string, keyType: string, key: unknown): Uint8Array {
  const prefix = Buffer.from((APP_SPEC.state.maps.box as Record<string, { prefix: string }>)[map]!.prefix, "base64");
  const keyBytes = algosdk.ABIType.from(keyType).encode(key as algosdk.ABIValue);
  return new Uint8Array([...prefix, ...keyBytes]);
}

export function encodeStruct(structName: string, values: algosdk.ABIValue[]): Uint8Array {
  const elements = (APP_SPEC.structs as Record<string, { type: string }[]>)[structName]!;
  return algosdk.ABIType.from(`(${elements.map((e) => e.type).join(",")})`).encode(values);
}

export class FakeAlgod {
  readonly safes = new Map<number, FakeSafe>();
  readonly requests: string[] = [];
  /** Transactions of each simulate call, in order. */
  readonly simulated: algosdk.Transaction[][] = [];
  /** Set to make simulate report a failure. */
  failure: string | undefined;
  /** Resources a simulated transaction reports as touched. Default: none. */
  accessed: (index: number, txn: algosdk.Transaction) => algosdk.modelsv2.SimulateUnnamedResourcesAccessed | undefined = () => undefined;
  private server: Server | undefined;
  url = "";

  addSafe(safe: FakeSafe): void {
    this.safes.set(safe.appId, safe);
  }

  setBox(appId: number, name: Uint8Array, value: Uint8Array): void {
    this.safes.get(appId)!.boxes.set(b64(name), value);
  }

  async start(): Promise<void> {
    this.server = createServer((req, res) => {
      const url = new URL(req.url ?? "/", "http://x");
      this.requests.push(`${req.method} ${url.pathname}`);
      const send = (status: number, body: unknown) => {
        res.writeHead(status, { "content-type": "application/json" });
        res.end(JSON.stringify(body));
      };
      if (req.method === "POST" && url.pathname === "/v2/teal/compile") {
        const chunks: Buffer[] = [];
        req.on("data", (c: Buffer) => chunks.push(c));
        req.on("end", () => {
          const source = Buffer.concat(chunks).toString();
          const spec = APP_SPEC as unknown as { source: { approval: string; clear: string }; byteCode: { approval: string; clear: string } };
          const isApproval = source === Buffer.from(spec.source.approval, "base64").toString();
          send(200, { hash: "", result: isApproval ? spec.byteCode.approval : spec.byteCode.clear, sourcemap: { version: 3, sources: [], names: [], mappings: "" } });
        });
        return;
      }
      if (req.method === "POST" && url.pathname === "/v2/transactions/simulate") {
        const chunks: Buffer[] = [];
        req.on("data", (c: Buffer) => chunks.push(c));
        req.on("end", () => {
          const request = algosdk.decodeMsgpack(Buffer.concat(chunks), algosdk.modelsv2.SimulateRequest);
          const txns = request.txnGroups[0]!.txns;
          this.simulated.push(txns.map((t) => t.txn));
          const results = txns.map((signed, i) => {
            const accessed = this.accessed(i, signed.txn);
            return new algosdk.modelsv2.SimulateTransactionResult({
              txnResult: new algosdk.modelsv2.PendingTransactionResponse({ poolError: "", txn: signed }),
              ...(accessed ? { unnamedResourcesAccessed: accessed } : {}),
            });
          });
          const group = new algosdk.modelsv2.SimulateTransactionGroupResult({
            txnResults: results,
            appBudgetConsumed: 1234,
            ...(this.failure ? { failureMessage: this.failure, failedAt: [0] } : {}),
          });
          const body = algosdk.encodeMsgpack(new algosdk.modelsv2.SimulateResponse({ version: 2, lastRound: 1000, txnGroups: [group] }));
          res.writeHead(200, { "content-type": "application/msgpack" });
          res.end(Buffer.from(body));
        });
        return;
      }
      const app = /^\/v2\/applications\/(\d+)(\/box|\/boxes)?$/.exec(url.pathname);
      if (url.pathname === "/v2/transactions/params") {
        return send(200, { "consensus-version": "future", fee: 0, "genesis-hash": GENESIS_HASH, "genesis-id": "testnet-v1.0", "last-round": 1000, "min-fee": 1000 });
      }
      if (url.pathname === "/v2/status") return send(200, { "last-round": 1000, "catchup-time": 0, "time-since-last-round": 0 });
      if (app) {
        const safe = this.safes.get(Number(app[1]));
        if (!safe) return send(404, { message: "application does not exist" });
        if (!app[2]) {
          const g = safe.globals;
          const state = [
            { key: b64(Buffer.from("name")), value: { type: 1, bytes: b64(Buffer.from(g.name)), uint: 0 } },
            { key: b64(Buffer.from("ngid")), value: { type: 2, bytes: "", uint: Number(g.nextGroupId) } },
            { key: b64(Buffer.from("npid")), value: { type: 2, bytes: "", uint: Number(g.nextProposalId) } },
            { key: b64(Buffer.from("gcnt")), value: { type: 2, bytes: "", uint: Number(g.groupCount) } },
            { key: b64(Buffer.from("paused")), value: { type: 2, bytes: "", uint: 0 } },
            { key: b64(Buffer.from("ver")), value: { type: 1, bytes: b64(Buffer.from("3.0.0")), uint: 0 } },
          ];
          return send(200, {
            id: safe.appId,
            params: {
              creator: algosdk.getApplicationAddress(1).toString(),
              "approval-program": (APP_SPEC.byteCode as { approval: string }).approval,
              "clear-state-program": (APP_SPEC.byteCode as { clear: string }).clear,
              "global-state": state,
              "global-state-schema": { "num-uint": 7, "num-byte-slice": 3 },
              "local-state-schema": { "num-uint": 0, "num-byte-slice": 0 },
            },
          });
        }
        if (app[2] === "/boxes") return send(200, { boxes: [...safe.boxes.keys()].map((name) => ({ name })) });
        const name = url.searchParams.get("name")?.replace(/^b64:/, "") ?? "";
        const decoded = decodeURIComponent(name);
        const value = safe.boxes.get(decoded) ?? safe.boxes.get(decoded.replaceAll(" ", "+"));
        if (!value) return send(404, { message: "box not found" });
        return send(200, { round: 1000, name: decoded, value: b64(value) });
      }
      send(501, { message: `fake algod does not serve ${req.method} ${url.pathname}` });
    });
    await new Promise<void>((resolve) => this.server!.listen(0, "127.0.0.1", resolve));
    this.url = `http://127.0.0.1:${(this.server!.address() as AddressInfo).port}`;
  }

  async stop(): Promise<void> {
    await new Promise<void>((resolve) => this.server?.close(() => resolve()));
  }
}

export { u64 };
