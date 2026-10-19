using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Core.Evidence;

/// <summary>Append-only, hash-chained, signed evidence log. Other components call this and nothing else.</summary>
public interface IEvidenceLog
{
    /// <summary>
    /// Appends one entry to the conversation's chain. Concurrent appends to the same conversation are serialized and
    /// get consecutive seq values. <paramref name="anchorRef"/> is only set on a conversation's first entry and on the
    /// first entry after a resume (T-037 supplies it).
    /// </summary>
    /// <exception cref="CanonicalJsonException">The payload carries an integer above 2^53-1; encode it as a decimal string.</exception>
    Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default);

    /// <summary>Deletes payload and salt of one entry for privacy. Hashes and signature stay, so the chain still verifies.</summary>
    Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default);

    /// <summary>Deletes payload and salt of every entry of the conversation.</summary>
    Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>The conversation's entries in seq order.</summary>
    Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>Verifies the stored chain against the key store's published keys.</summary>
    Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default);
}

public sealed class EvidenceLog(ChatDbContext db, IEvidenceKeyStore keys, TimeProvider? time = null) : IEvidenceLog
{
    private const int MaxAttempts = 8;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);

        // Canonicalize first: rejects unsafe integers before anything is written, and fixes the stored form.
        var payloadText = JsonCanonicalizer.Canonicalize(payload);
        var payloadNode = JsonNode.Parse(payloadText);

        for (var attempt = 1; ; attempt++)
        {
            var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            EvidenceEntry? entry = null;
            try
            {
                // Per-conversation lock on PostgreSQL; the unique (conversationId, seq) index backs it up everywhere.
                if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({conversationId.ToString("D")}, 0))", ct);

                var last = await db.EvidenceEntries.AsNoTracking()
                    .Where(e => e.ConversationId == conversationId)
                    .OrderByDescending(e => e.Seq)
                    .Select(e => new { e.Seq, e.EntryHash })
                    .FirstOrDefaultAsync(ct);

                var seq = (last?.Seq ?? 0) + 1;
                var prevHash = last?.EntryHash ?? EvidenceHasher.GenesisPrevHash;
                var salt = EvidenceHasher.NewSalt();
                var payloadHash = EvidenceHasher.PayloadHash(salt, payloadNode);
                var entryHash = EvidenceHasher.EntryHash(conversationId, seq, prevHash, kind, payloadHash, anchorRef?.ToString("D"));
                var kid = await keys.GetActiveKidAsync(ct);
                var signedAt = EvidenceHasher.TruncateToMillis(_time.GetUtcNow());
                var signature = await keys.SignAsync(kid, EvidenceHasher.SigningInput(entryHash, kid, signedAt), ct);

                entry = new EvidenceEntry
                {
                    ConversationId = conversationId,
                    Seq = seq,
                    Kind = kind,
                    AnchorRef = anchorRef,
                    Payload = payloadText,
                    Salt = salt,
                    PayloadHash = payloadHash,
                    PrevHash = prevHash,
                    EntryHash = entryHash,
                    Kid = kid,
                    SignedAt = signedAt,
                    Signature = EvidenceVerifier.Base64Url(signature),
                };
                db.EvidenceEntries.Add(entry);
                await db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);
                return entry;
            }
            catch (DbUpdateException) when (tx is not null && attempt < MaxAttempts)
            {
                // Only a lost race on (conversationId, seq) is retried: after rolling back, the seq we tried must now
                // exist. Any other failure (foreign key, connection, ...) is rethrown unchanged. Inside a caller's
                // transaction nothing is retried, because PostgreSQL has aborted that transaction.
                if (entry is not null) db.Entry(entry).State = EntityState.Detached;
                await tx.DisposeAsync();
                tx = null;
                var raced = entry is not null && await db.EvidenceEntries.AsNoTracking()
                    .AnyAsync(e => e.ConversationId == conversationId && e.Seq == entry.Seq, ct);
                if (!raced) throw;
            }
            finally
            {
                if (tx is not null) await tx.DisposeAsync();
            }
        }
    }

    public async Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) =>
        await db.EvidenceEntries.Where(e => e.ConversationId == conversationId && e.Seq == seq)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct) > 0;

    public async Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) =>
        await db.EvidenceEntries.Where(e => e.ConversationId == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Payload, (string?)null).SetProperty(e => e.Salt, (string?)null), ct);

    public async Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) =>
        await db.EvidenceEntries.AsNoTracking().Where(e => e.ConversationId == conversationId).OrderBy(e => e.Seq).ToListAsync(ct);

    public async Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default)
    {
        var map = (await keys.GetPublicKeysAsync(ct)).ToDictionary(k => k.Kid);
        return EvidenceVerifier.Verify(await GetChainAsync(conversationId, ct), map);
    }
}
