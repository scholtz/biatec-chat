# Dependency advisories (BCHAT-046)

State after T-200. Re-check with:

```bash
dotnet list backend package --vulnerable --include-transitive
pnpm audit
```

## .NET

`MessagePack` (GHSA-qhrr-8q5h-9q3h, moderate) reaches the backend transitively. `Biatec.Chat.Api` and `Biatec.Chat.Tools` now reference the patched
`MessagePack 3.1.12` directly, which wins over the transitive version. `dotnet list package --vulnerable --include-transitive` reports nothing.

## npm

Transitive versions that the direct dependency has not moved to are forced to patched versions in [`pnpm-workspace.yaml`](../pnpm-workspace.yaml) (`overrides`):
`parse-duration`, `serialize-javascript`, `undici` (6.x line), `sharp`, `nanoid` (5.x line), `@fastify/busboy`, `uuid`, `postcss-selector-parser`, `tinypool`.
They all arrive through `biatec-concentrated-liquidity-amm` in the builders sidecar (IPFS client, docs tooling and image handling the sidecar never calls: it only
uses the library's transaction builders). `vitest` is on 4.1.x in every workspace (3.x had advisories in `vitest`, `@vitest/mocker` and `tinypool`).
Remove an override when the direct dependency ships the patched range; a stale override is harmless but hides the real tree.

## Remaining

| Package | Severity | Path | Why it stays |
|---|---|---|---|
| `braces <= 3.0.3` | high | `biatec-concentrated-liquidity-amm > @easyops-cn/docusaurus-search-local > @docusaurus/... > micromatch > braces` | The advisory lists no patched version. The path is the library's documentation-site tooling (Docusaurus build), not code the sidecar loads. Ask BiatecCLAMM to drop the docs tooling from its runtime `dependencies` (a sibling repository: consume, do not change). |

SBOM and image provenance are tracked separately (BCHAT-018).
