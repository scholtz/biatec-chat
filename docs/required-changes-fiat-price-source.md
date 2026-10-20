# Required change: fiat price source for the holdings card

Status: open. The holdings data card (T-067) supports fiat values, but nothing fills them yet.

- `HoldingsCardBuilder` (backend) emits `assets[].amount` only. `fiatValue` and `fiatCurrency` stay unset, because no price
  source exists in the backend (`wallet.balance` returns base-unit amounts, not prices).
- Consequence in production: the widget takes the amounts-only path. The total, allocation bar, share percentages, swatches
  and the concentration note (spec User Experience 10) are implemented and tested in the widget with fiat fixtures, but are
  dormant until a price source is wired in.
- Planned fix: T-069 (market snapshot capability) provides prices; the builder then fills `fiatValue` per asset and
  `fiatCurrency` from it. No protocol change is needed, the fields already exist on the `data_card` event.
