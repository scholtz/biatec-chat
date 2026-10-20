using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Caps;

/// <summary>Which counter a signing request was reserved against.</summary>
public enum CapKind { None, SoftCap, SafeApproval, SafeExecution }

/// <summary>
/// The decision for one signing request. <see cref="RequireConsent"/> goes into the wallet sign request
/// (<c>requireConsent</c>); when it is true nothing was reserved, because a phone-approved action is never counted
/// against the soft cap.
/// </summary>
public sealed record CapDecision(bool RequireConsent, CapKind Reserved, string? Reason)
{
    public static CapDecision None { get; } = new(false, CapKind.None, null);
}

/// <summary>One signing request as the caps see it. Only the OIDC <c>sub</c> identifies the user.</summary>
/// <param name="StepId">The action step being signed; it is also the id of the reservation row.</param>
/// <param name="ValueEur">The backend's predicted value of the user's own funds (for safe.approve: the payload's value).</param>
public sealed record CapRequest(Tenant Tenant, string OidcSub, Guid StepId, string Capability, decimal? ValueEur);

/// <summary>
/// Tenant soft cap and safe approval caps. Enforced by the backend in addition to, never instead of, Biatec OIDC's limits.
/// Reservation is an atomic compare-and-add in PostgreSQL (a transaction-scoped advisory lock on sub + tenant), so
/// concurrent reservations on any pod cannot exceed the cap.
/// </summary>
public interface ISpendCaps
{
    /// <summary>Before every signing request: reserve the action's value, or decide that it needs phone consent.</summary>
    Task<CapDecision> ReserveAsync(CapRequest request, CancellationToken ct = default);

    /// <summary>Biatec OIDC signed without a challenge: the reservation becomes a final row valued at <paramref name="valuedEur"/> when given.</summary>
    Task FinalizeAsync(Guid stepId, string? valuedEur, CancellationToken ct = default);

    /// <summary>No signature came back (step-up challenge, refusal, outage, failure): the reservation is released.</summary>
    Task ReleaseAsync(Guid stepId, CancellationToken ct = default);

    /// <summary>
    /// A request signed after phone consent. Never counted against the soft cap; a safe approval or execution is still
    /// recorded, since each cap counts every action of its kind.
    /// </summary>
    Task RecordConsentedAsync(CapRequest request, CancellationToken ct = default);

    /// <summary>Releases reservations whose step has expired and prunes rows older than the longest window plus one hour.</summary>
    Task<int> PruneAsync(CancellationToken ct = default);
}

public sealed class SpendCaps(ChatDbContext db, TimeProvider clock) : ISpendCaps
{
    public const string SafeApprove = "safe.approve";
    public const string SafeExecute = "safe.execute";

    /// <summary>Capabilities that move the safe's funds, not the user's: never counted in the user soft cap.</summary>
    public static CapKind KindOf(string capability) => capability switch
    {
        SafeApprove => CapKind.SafeApproval,
        SafeExecute => CapKind.SafeExecution,
        _ => CapKind.SoftCap,
    };

    public async Task<CapDecision> ReserveAsync(CapRequest request, CancellationToken ct = default)
    {
        var kind = KindOf(request.Capability);
        var value = Math.Max(0m, request.ValueEur ?? 0m);
        var now = clock.GetUtcNow();
        var cutoff = now - CapDefaults.Window;
        var (sub, tenantId) = (request.OidcSub, request.Tenant.Id);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"biatec-chat-caps:" + tenantId + ":" + sub}, 0))", ct);

        // A retried confirm of the same step replaces its earlier, unfinished reservation instead of adding to it.
        await db.SoftCapSpends.Where(x => x.Id == request.StepId && x.Status == SoftCapStatus.Reserved).ExecuteDeleteAsync(ct);
        if (kind != CapKind.SoftCap)
            await db.SafeApprovalCounts.Where(x => x.Id == request.StepId).ExecuteDeleteAsync(ct);

        // The user's soft cap applies to every signing request, safe ones included (they add zero to it).
        var spent = await db.SoftCapSpends.AsNoTracking()
            .Where(x => x.OidcSub == sub && x.TenantId == tenantId
                        && (x.Status == SoftCapStatus.Reserved || (x.SpentAt != null && x.SpentAt > cutoff)))
            .SumAsync(x => (decimal?)x.AmountEur, ct) ?? 0m;
        var cap = SoftCapPolicy.EffectiveCapEur(request.Tenant);
        var softValue = kind == CapKind.SoftCap ? value : 0m;
        if (spent + softValue > cap)
        {
            await tx.CommitAsync(ct);
            return new CapDecision(true, CapKind.None, "soft_cap");
        }

        CapDecision decision;
        switch (kind)
        {
            case CapKind.SoftCap:
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "SoftCapSpends" ("Id", "OidcSub", "TenantId", "Status", "ReservedAt", "SpentAt", "AmountEur", "ValueSource")
                    VALUES ({request.StepId}, {sub}, {tenantId}, {nameof(SoftCapStatus.Reserved)}, {now}, NULL, {value}, {"predicted"})
                    """, ct);
                decision = new CapDecision(false, CapKind.SoftCap, null);
                break;
            default:
                var caps = SafeCaps.Parse(request.Tenant.SafeApprovalCaps);
                var safeKind = kind == CapKind.SafeApproval ? SafeApprovalKind.Approve : SafeApprovalKind.Execute;
                var window = db.SafeApprovalCounts.AsNoTracking()
                    .Where(x => x.OidcSub == sub && x.TenantId == tenantId && x.Kind == safeKind && x.ApprovedAt > cutoff);
                var count = await window.CountAsync(ct);
                var over = kind == CapKind.SafeApproval
                    ? count + 1 > caps.ApprovalsPerHour || (await window.SumAsync(x => (decimal?)x.ValueEur, ct) ?? 0m) + value > caps.ApprovalEurPerHour
                    : count + 1 > caps.ExecutionsPerHour;
                if (over)
                {
                    decision = new CapDecision(true, CapKind.None, kind == CapKind.SafeApproval ? "safe_approval_cap" : "safe_execution_cap");
                    break;
                }
                await InsertSafeAsync(request.StepId, sub, tenantId, safeKind, kind == CapKind.SafeApproval ? value : 0m, now, ct);
                decision = new CapDecision(false, kind, null);
                break;
        }
        await tx.CommitAsync(ct);
        return decision;
    }

    public async Task FinalizeAsync(Guid stepId, string? valuedEur, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // Count what Biatec OIDC valued when it says; else the backend's prediction stays (valueSource "predicted").
        if (decimal.TryParse(valuedEur, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var oidc) && oidc >= 0)
            await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, SoftCapStatus.Final).SetProperty(x => x.SpentAt, now)
                    .SetProperty(x => x.AmountEur, oidc).SetProperty(x => x.ValueSource, "oidc"), ct);
        else
            await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, SoftCapStatus.Final).SetProperty(x => x.SpentAt, now), ct);
        // Safe rows were written at reservation and already count.
    }

    public async Task ReleaseAsync(Guid stepId, CancellationToken ct = default)
    {
        await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved).ExecuteDeleteAsync(ct);
        await db.SafeApprovalCounts.Where(x => x.Id == stepId).ExecuteDeleteAsync(ct);
    }

    public async Task RecordConsentedAsync(CapRequest request, CancellationToken ct = default)
    {
        var kind = KindOf(request.Capability);
        if (kind == CapKind.SoftCap) return;
        await InsertSafeAsync(request.StepId, request.OidcSub, request.Tenant.Id,
            kind == CapKind.SafeApproval ? SafeApprovalKind.Approve : SafeApprovalKind.Execute,
            kind == CapKind.SafeApproval ? Math.Max(0m, request.ValueEur ?? 0m) : 0m, clock.GetUtcNow(), ct);
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var old = now - CapDefaults.Retention;
        // A reservation is released when its preview expires: its step's group window has passed (or the step is gone).
        var released = await db.SoftCapSpends
            .Where(x => x.Status == SoftCapStatus.Reserved
                        && (x.ReservedAt < old || !db.ActionSteps.Any(s => s.Id == x.Id && s.ExpiresAt > now)))
            .ExecuteDeleteAsync(ct);
        var finals = await db.SoftCapSpends.Where(x => x.Status == SoftCapStatus.Final && x.SpentAt < old).ExecuteDeleteAsync(ct);
        var safes = await db.SafeApprovalCounts.Where(x => x.ApprovedAt < old).ExecuteDeleteAsync(ct);
        return released + finals + safes;
    }

    private Task<int> InsertSafeAsync(Guid id, string sub, string tenantId, SafeApprovalKind kind, decimal value, DateTimeOffset at, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SafeApprovalCounts" ("Id", "OidcSub", "TenantId", "Kind", "ValueEur", "ApprovedAt")
            VALUES ({id}, {sub}, {tenantId}, {kind.ToString()}, {value}, {at})
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
}
