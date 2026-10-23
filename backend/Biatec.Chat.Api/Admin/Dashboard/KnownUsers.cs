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
            if (await BumpAsync(db, tenantId, identity.Subject, now, ct) > 0) return;
            db.KnownUsers.Add(new KnownUser
            {
                Id = Guid.NewGuid(), TenantId = tenantId, OidcSub = identity.Subject,
                FirstSeenAt = now, LastSeenAt = now, SignInCount = 1,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // A concurrent sign-in of the same user inserted first (unique index): count this one on top of it.
                db.ChangeTracker.Clear();
                await BumpAsync(db, tenantId, identity.Subject, now, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            log.LogWarning("Could not record the user for the admin dashboard: {ErrorType}", ex.GetType().Name);
        }
    }

    private static Task<int> BumpAsync(ChatDbContext db, string tenantId, string sub, DateTimeOffset now, CancellationToken ct) =>
        db.KnownUsers.Where(u => u.TenantId == tenantId && u.OidcSub == sub)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.SignInCount, x => x.SignInCount + 1)
                , ct);
}
