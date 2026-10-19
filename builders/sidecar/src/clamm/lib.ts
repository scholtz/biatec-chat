import { createRequire } from "node:module";

/**
 * algosdk, AlgoKit and biatec-concentrated-liquidity-amm, all loaded as CommonJS from one place. algosdk and AlgoKit
 * ship separate ESM and CJS builds and check addresses and transactions with instanceof, so objects made by an ESM
 * copy are rejected by the CJS copy the CLAMM library uses ("Not an address"). Every CLAMM module imports from here.
 */
const require = createRequire(import.meta.url);

export const algosdk = require("algosdk") as typeof import("algosdk");
export const { AlgorandClient } = require("@algorandfoundation/algokit-utils") as typeof import("@algorandfoundation/algokit-utils");
export const clamm = require("biatec-concentrated-liquidity-amm") as typeof import("biatec-concentrated-liquidity-amm");
