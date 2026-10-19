# Evidence anchoring (T-037)

- `MerkleTree` / `AnchorNote`: the fixed tree and the note (`{v, root, seq, prev, gapFrom}`, JCS, at most 1024 bytes).
- `AnchorService.TickAsync`: one tick. `AnchorHostedService` runs it every 10 minutes when `Evidence:Anchoring:Enabled` is true.
  Needs `Evidence:Anchoring:AccountMnemonic` (env var or secret store only), `Evidence:Anchoring:Network` (default `testnet`)
  and the matching `Algod:Networks:<network>` endpoint. Fund the evidence account on testnet only.
- `AnchoredEvidenceWriter`: first entry (`anchorRef`), `resume` entry, `end` marker.
- `AnchorExporter` gives T-036 the anchors and inclusion proofs for the bundle; `AnchorVerifier.Verify` checks them
  against the anchors read from the chain (indexer) and parsed with `AnchorNote.TryParse`.

Deviation: `EvidenceEntry.AnchorRef` is a `Guid?` column from T-034, so it holds the `EvidenceAnchor.Id`, not the
transaction ID. The bundle's `anchors` list maps that id to the transaction ID, and the verifier resolves it against the
chain. Changing the column to the transaction ID string needs a migration and a change in the evidence log.

## Reliability (review round 1)

- `AlgodAnchorBroadcaster` is idempotent per note. It keeps the signed transaction until it confirmed or its LastValid
  round passed; a retry first checks the node (`/v2/transactions/pending/{txid}`), re-submits the same bytes while still
  valid, and sends a new transaction only after the first can no longer land. A timeout after submit is therefore never
  turned into a second anchor with the same `seq`.
- `EvidenceAnchorAttempts` (migration `AnchorAttempts`) makes the sequence number durable: the attempt is stored before
  the broadcast, marked `lost` when unconfirmed (the next note's `gapFrom` is derived from the table, so a restart does
  not forget it), and given the tx id as soon as it confirms. If storing the anchor and proofs then fails, the half-added
  entities are cleared and the next tick rebuilds the anchor and proofs from the stored leaves. Assumes one anchoring instance.
- Residual: an attempt marked lost whose transaction lands later (for example after a long outage) is an on-chain anchor
  that the database does not list; the verifier works from the chain list it is given and the declared gap.

## Wired vs deferred

Wired: tick job, broadcaster, gap handling, exporter and verifier. Not yet called from the conversation flow:
`AnchoredEvidenceWriter.AppendFirstAsync / ResumeIfDroppedAsync / EndAsync`, and the evidence bundle integration (T-036).
A testnet run is not recorded: it needs a funded evidence account, which this environment does not have. The broadcaster is
covered against a fake Algod (transaction shape, idempotent retry, expiry). To run on testnet: set
`Evidence__Anchoring__Enabled=true`, `Evidence__Anchoring__AccountMnemonic` (secret store), and fund the account from the testnet dispenser.

## anchorRef deviation (needs user sign-off)

The spec defines `anchorRef` as the transaction ID. `EvidenceEntry.AnchorRef` is a `Guid?` from T-034, so it holds the
`EvidenceAnchor.Id`, and that id is part of `entryHash`. Switching to the tx id string changes the T-034 column, the
entry hash input and `EvidenceVerifier`, which other tasks build on, so it is left for the user to approve. Until then a
verifier must not trust the bundle's id-to-tx mapping on its own: `AnchorVerifier` checks each referenced anchor against
the chain-read anchor list (sequence, tx id, root, prev, gap).
