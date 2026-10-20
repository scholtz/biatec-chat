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

`EvidenceEntry.AnchorRef` holds the transaction ID of the latest confirmed anchor (see "anchorRef" below).

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

## Integration points for the chat flow and the export bundle

- Chat flow: first entry of a conversation via `AnchoredEvidenceWriter.AppendFirstAsync`; the first entry after the
  conversation was dropped from ticks via `ResumeIfDroppedAsync`; closing a conversation via `EndAsync`. Entries appended
  without the writer have no `anchorRef` and no `resume`.
- Evidence export (T-036): call `AnchorExporter.ExportAsync(db, conversationId)` and put its `Proofs` and `Anchors` in the
  bundle ("Bundle includes proofs"); verify with `AnchorVerifier.Verify`.

## Wired vs deferred

Tick cost: a tick only reads conversations with an entry since a cutoff reaching back past the last confirmed anchor, not all history.

Wired: tick job, broadcaster, gap handling, exporter and verifier, and the first entry of every conversation:
`EvidenceRecorder` (`Evidence/Recording/`, T-035) calls `AnchoredEvidenceWriter.AppendFirstAsync` for a conversation's
first entry, so it carries `anchorRef`. The anchoring job runs in stage and production (`k8s/base/backend.yaml`,
`Evidence__Anchoring__Enabled: "true"`, testnet) once the evidence-account secret exists (k8s/README.md).

Not wired yet: `ResumeIfDroppedAsync` and `EndAsync` are not called from the conversation flow (so there are no `resume`
entries or `end` markers), and the export bundle (`Evidence/Export/EvidenceBundleBuilder.cs`, T-036) carries each entry's
`anchorRef` but not `AnchorExporter`'s anchors and inclusion proofs. A testnet run is not recorded in this repository. The
broadcaster is covered against a fake Algod (transaction shape, idempotent retry, expiry). To run on testnet: set
`Evidence__Anchoring__Enabled=true`, `Evidence__Anchoring__AccountMnemonic` (secret store), and fund the account from the
testnet dispenser.

## anchorRef (T-081)

`EvidenceEntry.AnchorRef` is the anchor's Algorand transaction ID (string), stored, hashed (`entryHash`), exported and
resolved by `AnchorVerifier` directly against the chain. Migration `EvidenceAnchorRefTxId` converts the column from `uuid` to
text; rows written before it keep their GUID text (the hash they were written with, so the chain stays valid) and are
legacy: their anchorRef is not a transaction ID and the anchor verifier does not resolve it.
