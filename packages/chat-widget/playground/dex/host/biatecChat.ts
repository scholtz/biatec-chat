// The BiatecDEX-side code of docs/integration-biatec-dex.md steps 3 and 4, as the DEX would write it (step 1 is in
// ../main.ts, step 2 in ./i18n.ts, step 5 in ../DexApp.vue). It imports the package by name:
// `pnpm typecheck:playground` compiles it against the built dist/index.d.ts, and test/dex-integration.test.ts runs it
// and checks that every line of the doc's snippets appears in the playground.
import { useBiatecChat, type AskResult } from '@biatec/chat-widget'

/** Step 3: what the trading and liquidity routes show. Cleared when leaving them. */
export function setTradingContext(network: string, base: string, quote: string, poolAppId: number) {
  useBiatecChat().setPageContext(`Biatec DEX · ${network} · pair ${base}/${quote} · pool app ${poolAppId}`)
}

export function clearTradingContext() {
  useBiatecChat().setPageContext(undefined)
}

/** Step 4: the "Ask Biatec AI" links under the order card and the add-liquidity form. */
export function askAboutOrder(side: "buy" | "sell", amount: number, base: string, quote: string): Promise<AskResult> {
  if (side === "sell") return useBiatecChat().ask(`Swap ${amount} ${base} to ${quote} on Biatec DEX`)
  return useBiatecChat().ask(`Swap ${amount} ${quote} to ${base} on Biatec DEX`)
}

export function askAboutLiquidity(base: string, quote: string): Promise<AskResult> {
  return useBiatecChat().ask(`Add liquidity to the Biatec DEX ${base}/${quote} pool`)
}
