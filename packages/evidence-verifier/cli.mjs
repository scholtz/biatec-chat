#!/usr/bin/env node
// Entry point: node cli.mjs verify bundle.json --jwks jwks.json
import { main } from "./dist/cli.js";

process.exitCode = await main(process.argv.slice(2));
