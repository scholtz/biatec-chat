# Required change: fiat price source for the holdings card

Status: open. The holdings data card (T-067) supports fiat values, but nothing fills them yet.

- `HoldingsCardBuilder` (backend) emits `assets[].amount` only. `fiatValue` and `fiatCurrency` stay unset, because no price
  source exists in the backend (`wallet.balance` returns base-unit amounts, not prices).
- Consequence in production: the widget takes the amounts-only path. The total, allocation bar, share percentages, swatches
  and the concentration note (spec User Experience 10) are implemented and tested in the widget with fiat fixtures, but are
  dormant until a price source is wired in.
- Not fixed by T-069: `market.snapshot` (`backend/Biatec.Chat.Api/Market/`) shows one pair's 24 hour market data from
  public Biatec DEX data, but it is not wired into `HoldingsCardBuilder`. A fix still needs a price source in the builder,
  which then fills `fiatValue` per asset and `fiatCurrency`. No protocol change is needed, the fields already exist on the
  `data_card` event.
