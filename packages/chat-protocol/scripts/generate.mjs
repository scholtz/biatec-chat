// Regenerates openapi.json from the backend, then src/generated/schema.ts from openapi.json.
// Needs the .NET 10 SDK. Pass --skip-backend to only regenerate the TypeScript from the committed openapi.json.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { mkdirSync } from "node:fs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const run = (cmd, args, cwd = root) => {
  const r = spawnSync(cmd, args, { cwd, stdio: "inherit", shell: process.platform === "win32" });
  if (r.status !== 0) process.exit(r.status ?? 1);
};

if (!process.argv.includes("--skip-backend")) {
  const api = join(root, "..", "..", "backend", "Biatec.Chat.Api");
  run("dotnet", ["build", "-p:OpenApiGenerateDocuments=true", "--nologo"], api);
}
mkdirSync(join(root, "src", "generated"), { recursive: true });
run("pnpm", ["exec", "openapi-typescript", "openapi.json", "-o", "src/generated/schema.ts"]);
