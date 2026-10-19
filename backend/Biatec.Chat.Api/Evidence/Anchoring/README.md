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
