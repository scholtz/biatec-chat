using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Core.Data;

/// <summary>
/// The per-conversation lock (PostgreSQL transaction advisory lock) that serializes evidence appends, message stores and erasure of one
/// conversation, so a turn already in flight cannot write readable data after the conversation was erased (BCHAT-013).
/// Held until the surrounding transaction ends. A no-op on other providers, where the unique indexes back the callers up.
/// </summary>
public static class ConversationLock
{
    public static async Task AcquireAsync(ChatDbContext db, Guid conversationId, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({conversationId.ToString("D")}, 0))", ct);
    }
}
