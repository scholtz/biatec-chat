import { readFile } from "node:fs/promises";
import { verifyBundle, type Jwks } from "./verify.js";

const USAGE = `Usage: biatec-evidence-verify verify <bundle.json | https://url> --jwks <jwks.json | https://url> [--json]   (http:// is refused; redirects are refused)
       biatec-evidence-verify verify <bundle.json> --insecure-embedded-keys [--json]   (self-consistency only)

Exit codes: 0 verified, 1 verification failed, 2 usage or input error.`;

async function load(source: string): Promise<unknown> {
  // A JWKS (or bundle) fetched over plain http can be swapped on the way, which would defeat the point of verifying against it.
  if (/^http:\/\//i.test(source)) throw new Error(`${source} is not https; refusing to fetch it.`);
  if (/^https:\/\//i.test(source)) {
    // Redirects are an error: an https URL must not hand the request to plain http (or anywhere else) behind the caller's back.
    const res = await fetch(source, { redirect: "error" });
    if (!res.ok) throw new Error(`GET ${source} returned ${res.status}`);
    return res.json();
  }
  return JSON.parse(await readFile(source, "utf8"));
}

export async function main(argv: string[], out: (s: string) => void = console.log, err: (s: string) => void = console.error): Promise<number> {
  const [cmd, file, ...rest] = argv;
  if (cmd !== "verify" || !file) { err(USAGE); return 2; }
  let jwksSource: string | undefined;
  let embedded = false;
  let json = false;
  for (let i = 0; i < rest.length; i++) {
    const a = rest[i];
    if (a === "--jwks") { jwksSource = rest[++i]; if (!jwksSource) { err(USAGE); return 2; } }
    else if (a === "--insecure-embedded-keys") embedded = true;
    else if (a === "--json") json = true;
    else { err(`Unknown option ${a}\n${USAGE}`); return 2; }
  }
  if (!jwksSource && !embedded) { err(USAGE); return 2; }

  let bundle: unknown;
  let jwks: Jwks | undefined;
  try {
    bundle = await load(file);
    if (jwksSource) jwks = (await load(jwksSource)) as Jwks;
  } catch (e) {
    err(`Cannot read input: ${(e as Error).message}`);
    return 2;
  }

  const r = verifyBundle(bundle, { jwks, allowEmbeddedKeys: embedded && !jwks });
  if (json) out(JSON.stringify(r, null, 2));
  else {
    for (const w of r.warnings) out(`WARNING: ${w}`);
    for (const e of r.errors) out(`FAIL: ${e}`);
    out(r.valid
      ? `OK: ${r.entries} entries verified (${r.payloadsVerified} payloads recomputed, ${r.payloadsDeleted} deleted payloads taken from the stored hash)${r.keysAuthenticated ? ", keys match the JWKS" : ", keys NOT authenticated"}; ${r.anchoredEntries} entries claim an anchor, ${r.unanchoredEntries} do not${r.anchorsResolved ? "; anchors checked" : "; anchors were NOT checked"}.`
      : "INVALID: the bundle did not verify.");
  }
  return r.valid ? 0 : 1;
}
