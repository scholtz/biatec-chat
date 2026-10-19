# Evidence anchoring (T-037)

- `MerkleTree` / `AnchorNote`: the fixed tree and the note (`{v, root, seq, prev, gapFrom}`, JCS, at most 1024 bytes).
- `AnchorService.TickAsync`: one tick. `AnchorHostedService` runs it every 10 minutes when `Evidence:Anchoring:Enabled` is true.
  Needs `Evidence:Anchoring:AccountMnemonic` (env var or secret store only), `Evidence:Anchoring:Network` (default `testnet`)
  and the matching `Algod:Networks:<network>` endpoint. Fund the evidence account on testnet only.
  Also set `Evidence:Anchoring:IndexerUrl` (and optionally `IndexerToken`): algod's pending endpoint forgets confirmed
  transactions quickly, so the indexer is how a transaction that confirmed after a timeout is still found. Without it, a
  late-confirmed anchor can be misread as lost.
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
- Lost is definitive (review round 2): the anchor transaction is valid for `ValidRounds` (40) rounds only. After failed
  confirmations the service asks the broadcaster (`ResolveAsync`) before deciding: confirmed means the anchor is completed,
  still valid means the attempt stays `pending`, no new anchor is started and the next tick checks again, expired means
  lost (and the broadcaster drops its `_submitted` entry). After a restart the broadcaster does not know the transaction;
  the attempt then stays pending until `UnknownGrace` (5 min) after its creation.

## Integration points for T-036 and the chat flow (must adopt)

- Chat flow: first entry of a conversation via `AnchoredEvidenceWriter.AppendFirstAsync`; the first entry after the
  conversation was dropped from ticks via `ResumeIfDroppedAsync`; closing a conversation via `EndAsync`. Entries appended
  without the writer have no `anchorRef` and no `resume`.
- Evidence export (T-036): call `AnchorExporter.ExportAsync(db, conversationId)` and put its `Proofs` and `Anchors` in the
  bundle ("Bundle includes proofs"); verify with `AnchorVerifier.Verify`.

## Wired vs deferred

Open acceptance criteria (not ticked, do not tick until done): a recorded testnet run, calling `AnchoredEvidenceWriter` from the
conversation flow, and embedding `AnchorExporter` output in the export bundle with an end-to-end test (hand-off to T-036).

Tick cost: a tick only reads conversations with an entry since a cutoff reaching back past the last confirmed anchor, not all history.

Wired: tick job, broadcaster, gap handling, exporter and verifier. Not yet called from the conversation flow:
`AnchoredEvidenceWriter.AppendFirstAsync / ResumeIfDroppedAsync / EndAsync`, and the evidence bundle integration (T-036).
A testnet run is not recorded: it needs a funded evidence account, which this environment does not have. The broadcaster is
covered against a fake Algod (transaction shape, idempotent retry, expiry). To run on testnet: set
`Evidence__Anchoring__Enabled=true`, `Evidence__Anchoring__AccountMnemonic` (secret store), and fund the account from the testnet dispenser.

## anchorRef deviation (needs user sign-off)

The spec defines `anchorRef` as the transaction ID. `EvidenceEntry.AnchorRef` is a `Guid?` from T-034, so it holds the
`EvidenceAnchor.Id`, and that id is part of `entryHash`. Switching to the tx id string changes the T-034 column, the
entry hash input and `EvidenceVerifier`, which other tasks build on, so it is left for the user to approve. Until then a
verifier must not trust the bundle's id-to-tx mapping: `EvidenceAnchor.Id` is `AnchorIds.FromTxId(txId)` (first 16 bytes of
SHA-256 of the tx id), `AnchorVerifier` resolves every `anchorRef` by recomputing that id from the chain's anchors, and it
rejects any bundle anchor record whose id, sequence, root, prev or gap differs from the chain. The operator therefore cannot
re-label which on-chain anchor an entry points to. Anchor rows created before this change keep random ids.
