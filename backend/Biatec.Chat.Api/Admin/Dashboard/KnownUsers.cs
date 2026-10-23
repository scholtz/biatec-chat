using Biatec.Chat.Api.Auth;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Admin.Dashboard;

/// <summary>The dashboard's list of users: one <see cref="KnownUser"/> row per (tenant, subject), kept current at every sign-in.</summary>
public static class KnownUsers
{
    /// <summary>
    /// Records a sign-in: creates the row or bumps it (last seen, count). Bookkeeping for the dashboard must never fail a
    /// user's sign-in, so a database error is logged (type only) and swallowed.
    /// </summary>
    public static async Task RecordSignInAsync(ChatDbContext db, string tenantId, IdTokenIdentity identity, DateTimeOffset now, ILogger log, CancellationToken ct)
    {
        if (identity.Subject.Length > 256 || tenantId.Length > 128) return;
        try
        {
            // One atomic upsert (also correct for two sign-ins at once); it touches no tracked entity of the request's context.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""KnownUsers"" (""Id"", ""TenantId"", ""OidcSub"", ""FirstSeenAt"", ""LastSeenAt"", ""SignInCount"")
                VALUES ({Guid.NewGuid()}, {tenantId}, {identity.Subject}, {now}, {now}, 1)
                ON CONFLICT (""TenantId"", ""OidcSub"") DO UPDATE
                SET ""LastSeenAt"" = EXCLUDED.""LastSeenAt"", ""SignInCount"" = ""KnownUsers"".""SignInCount"" + 1", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not record the user for the admin dashboard: {ErrorType}", ex.GetType().Name);
        }
    }
}
