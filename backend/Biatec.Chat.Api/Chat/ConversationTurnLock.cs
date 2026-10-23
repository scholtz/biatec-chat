using System.Globalization;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Api.Chat;

/// <summary>
/// One turn at a time per conversation across ALL replicas (BCHAT-039). The in-process check in the chat endpoint only covers one
/// pod; with two, concurrent turns would allocate the same message Seq or interleave history. A PostgreSQL session advisory lock is held
/// on a dedicated connection for the whole turn. That connection is not pooled, so closing it always releases the lock, even when the
/// unlock is never reached (a crash, an abort). Other providers (the in-memory tests) get a no-op handle.
/// Needs a direct PostgreSQL connection: a transaction-mode pooler (PgBouncer, RDS Proxy) could run the lock and the connection's life on different
/// backends, which would silently break the guarantee. The connection string the backend already uses for EF is reused, so the same rule applies there.
/// </summary>
public static class ConversationTurnLock
{
    /// <summary>The lock, or null when another turn of this conversation holds it. Dispose to release.</summary>
    public static async Task<IAsyncDisposable?> TryAcquireAsync(ChatDbContext db, Guid conversationId, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true) return NoLock.Instance;
        var cs = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false }.ConnectionString;
        var conn = new NpgsqlConnection(cs);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", conn);
            cmd.Parameters.AddWithValue("key", "turn:" + conversationId.ToString("D", CultureInfo.InvariantCulture));
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return new Held(conn);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        await conn.DisposeAsync();
        return null;
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Held(NpgsqlConnection conn) : IAsyncDisposable
    {
        // No explicit unlock round trip: it could hang on a half-dead connection, and closing the non-pooled connection releases the lock.
        public async ValueTask DisposeAsync()
        {
            try { await conn.DisposeAsync(); }
            catch { /* the server drops the session, and with it the lock */ }
        }
    }
}
