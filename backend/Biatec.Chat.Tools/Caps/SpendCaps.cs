using System.Globalization;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Caps;

/// <summary>Which counter a signing request was reserved against.</summary>
public enum CapKind { None, SoftCap, SafeApproval, SafeExecution }

/// <summary>
/// The decision for one signing request. <see cref="RequireConsent"/> goes into the wallet sign request
/// (<c>requireConsent</c>); when it is true nothing was reserved, because a phone-approved action is never counted
/// against the soft cap. <see cref="Reason"/> names the cap that forced it (soft_cap, safe_approval_cap, safe_execution_cap).
/// </summary>
/// <param name="Refused">T-061: refused outright (the reduced cap for a user without an active device); nothing was reserved.</param>
public sealed record CapDecision(bool RequireConsent, CapKind Reserved, string? Reason, bool Refused = false)
{
    public static CapDecision None { get; } = new(false, CapKind.None, null);
}

/// <summary>One signing request as the caps see it. Only the OIDC <c>sub</c> identifies the user.</summary>
/// <param name="StepId">The action step being signed; it is also the id of the reservation row.</param>
/// <param name="ValueEur">The backend's predicted value of the user's own funds (for safe.approve: the payload's value).</param>
/// <param name="NoDeviceDailyCapEur">T-061: set for a user without an active device; the day's own-funds total must stay within it.</param>
public sealed record CapRequest(Tenant Tenant, string OidcSub, Guid StepId, string Capability, decimal? ValueEur, decimal? NoDeviceDailyCapEur = null);

public sealed class CapsOptions
{
    public const string Section = "Caps";

    /// <summary>
    /// Whether Biatec OIDC honours <c>requireConsent</c> on the signing request (docs/required-changes-biatec-oidc.md
    /// row 3, fallback "None": only the mock does today). False (the default): an action a cap would send to the phone
    /// is refused instead, so the cap is a hard stop for the rest of the window against real Biatec OIDC.
    /// </summary>
    public bool OidcHonoursRequireConsent { get; set; }
}

/// <summary>
/// Tenant soft cap and safe approval caps. Enforced by the backend in addition to, never instead of, Biatec OIDC's limits.
/// Reservation is an atomic compare-and-add in PostgreSQL (a transaction-scoped advisory lock on sub + tenant), so
/// concurrent reservations on any pod cannot exceed the cap.
/// </summary>
public interface ISpendCaps
{
    /// <inheritdoc cref="CapsOptions.OidcHonoursRequireConsent"/>
    bool OidcHonoursRequireConsent { get; }

    /// <summary>Before every signing request: reserve the action's value, or decide that it needs phone consent.</summary>
    Task<CapDecision> ReserveAsync(CapRequest request, CancellationToken ct = default);

    /// <summary>Biatec OIDC signed without a challenge: the reservation becomes final, valued at <paramref name="valuedEur"/> when given.</summary>
    Task FinalizeAsync(Guid stepId, string? valuedEur, CancellationToken ct = default);

    /// <summary>No signature came back (step-up challenge, refusal, outage, failure): the reservation is released.</summary>
    Task ReleaseAsync(Guid stepId, CancellationToken ct = default);

    /// <summary>Signed, but provably nothing moved (not broadcast, rejected, expired): every row of the step is removed, final ones too.</summary>
    Task RevokeAsync(Guid stepId, CancellationToken ct = default);

    /// <summary>
    /// A request signed after phone consent. Never counted against the soft cap; a safe approval or execution is still
    /// recorded, since each cap counts every action of its kind.
    /// </summary>
    Task RecordConsentedAsync(CapRequest request, string? valuedEur, CancellationToken ct = default);

    /// <summary>Releases reservations whose step has expired or is gone, and prunes rows older than the longest window plus one hour.</summary>
    Task<int> PruneAsync(CancellationToken ct = default);
}

/// <remarks>
/// A safe action in flight is a <c>Reserved</c> <see cref="SoftCapSpend"/> row whose <c>ValueSource</c> marks its kind
/// (<see cref="SafeApproveReserved"/>, <see cref="SafeExecuteReserved"/>). It counts toward its safe cap only, never the
/// soft cap, and shares the soft cap's lifecycle (released on the known paths and by the pruner once its step expired);
/// when Biatec OIDC signs, it becomes a <see cref="SafeApprovalCount"/> row.
/// </remarks>
public sealed class SpendCaps(ChatDbContext db, TimeProvider clock, CapsOptions? options = null) : ISpendCaps
{
    public const string SafeApprove = "safe.approve";
    public const string SafeExecute = "safe.execute";
    public const string SafeApproveReserved = "reserved:safe.approve";
    public const string SafeExecuteReserved = "reserved:safe.execute";

    public bool OidcHonoursRequireConsent => options?.OidcHonoursRequireConsent ?? false;

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
        var value = kind == CapKind.SafeExecution ? 0m : Math.Max(0m, request.ValueEur ?? 0m);
        var now = clock.GetUtcNow();
        var cutoff = now - CapDefaults.Window;
        var (sub, tenantId) = (request.OidcSub, request.Tenant.Id);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"biatec-chat-caps:" + tenantId + ":" + sub}, 0))", ct);

        // A retried confirm of the same step replaces its earlier, unfinished reservation instead of adding to it.
        await db.SoftCapSpends.Where(x => x.Id == request.StepId && x.Status == SoftCapStatus.Reserved).ExecuteDeleteAsync(ct);

        var mine = db.SoftCapSpends.AsNoTracking().Where(x => x.OidcSub == sub && x.TenantId == tenantId);
        // The user's soft cap applies to every signing request, safe ones included (they add zero to it).
        var spent = await mine
            .Where(x => x.ValueSource != SafeApproveReserved && x.ValueSource != SafeExecuteReserved
                        && (x.Status == SoftCapStatus.Reserved || (x.SpentAt != null && x.SpentAt > cutoff)))
            .SumAsync(x => (decimal?)x.AmountEur, ct) ?? 0m;
        var softValue = kind == CapKind.SoftCap ? value : 0m;
        // T-061: reduced daily cap for a user without an active device, checked under the same lock.
        if (request.NoDeviceDailyCapEur is { } dailyCap
            && (await NoDevice.NoDevicePolicy.CountedToday(mine, now).SumAsync(x => (decimal?)x.AmountEur, ct) ?? 0m) + softValue > dailyCap)
        {
            await tx.CommitAsync(ct);
            return new CapDecision(false, CapKind.None, NoDevice.NoDevicePolicy.DailyCapReason, Refused: true);
        }
        if (spent + softValue > SoftCapPolicy.EffectiveCapEur(request.Tenant))
        {
            await tx.CommitAsync(ct);
            return new CapDecision(true, CapKind.None, "soft_cap");
        }

        if (kind != CapKind.SoftCap)
        {
            var caps = SafeCaps.Parse(request.Tenant.SafeApprovalCaps);
            var (safeKind, marker) = kind == CapKind.SafeApproval
                ? (SafeApprovalKind.Approve, SafeApproveReserved) : (SafeApprovalKind.Execute, SafeExecuteReserved);
            var counted = db.SafeApprovalCounts.AsNoTracking()
                .Where(x => x.OidcSub == sub && x.TenantId == tenantId && x.Kind == safeKind && x.ApprovedAt > cutoff);
            var inFlight = mine.Where(x => x.Status == SoftCapStatus.Reserved && x.ValueSource == marker);
            var count = await counted.CountAsync(ct) + await inFlight.CountAsync(ct);
            var over = kind == CapKind.SafeApproval
                ? count + 1 > caps.ApprovalsPerHour
                  || (await counted.SumAsync(x => (decimal?)x.ValueEur, ct) ?? 0m) + (await inFlight.SumAsync(x => (decimal?)x.AmountEur, ct) ?? 0m) + value > caps.ApprovalEurPerHour
                : count + 1 > caps.ExecutionsPerHour;
            if (over)
            {
                await tx.CommitAsync(ct);
                return new CapDecision(true, CapKind.None, kind == CapKind.SafeApproval ? "safe_approval_cap" : "safe_execution_cap");
            }
            await InsertReservedAsync(request.StepId, sub, tenantId, value, marker, now, ct);
        }
        else
        {
            await InsertReservedAsync(request.StepId, sub, tenantId, value, "predicted", now, ct);
        }
        await tx.CommitAsync(ct);
        return new CapDecision(false, kind, null);
    }

    public async Task FinalizeAsync(Guid stepId, string? valuedEur, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var oidc = ParseEur(valuedEur);
        var row = await db.SoftCapSpends.AsNoTracking().FirstOrDefaultAsync(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved, ct);
        if (row is null) return;
        if (row.ValueSource is SafeApproveReserved or SafeExecuteReserved)
        {
            var approve = row.ValueSource == SafeApproveReserved;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await InsertSafeAsync(stepId, row.OidcSub, row.TenantId, approve ? SafeApprovalKind.Approve : SafeApprovalKind.Execute,
                approve ? Math.Max(row.AmountEur, oidc ?? 0m) : 0m, now, ct);
            await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }
        // Count what Biatec OIDC valued when it says; else the backend's prediction stays (valueSource "predicted").
        if (oidc is { } v)
            await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, SoftCapStatus.Final).SetProperty(x => x.SpentAt, now)
                    .SetProperty(x => x.AmountEur, v).SetProperty(x => x.ValueSource, "oidc"), ct);
        else
            await db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, SoftCapStatus.Final).SetProperty(x => x.SpentAt, now), ct);
    }

    public Task ReleaseAsync(Guid stepId, CancellationToken ct = default) =>
        db.SoftCapSpends.Where(x => x.Id == stepId && x.Status == SoftCapStatus.Reserved).ExecuteDeleteAsync(ct);

    public async Task RevokeAsync(Guid stepId, CancellationToken ct = default)
    {
        await db.SoftCapSpends.Where(x => x.Id == stepId).ExecuteDeleteAsync(ct);
        await db.SafeApprovalCounts.Where(x => x.Id == stepId).ExecuteDeleteAsync(ct);
    }

    public async Task RecordConsentedAsync(CapRequest request, string? valuedEur, CancellationToken ct = default)
    {
        var kind = KindOf(request.Capability);
        if (kind == CapKind.SoftCap) return;
        var approve = kind == CapKind.SafeApproval;
        await InsertSafeAsync(request.StepId, request.OidcSub, request.Tenant.Id, approve ? SafeApprovalKind.Approve : SafeApprovalKind.Execute,
            approve ? Math.Max(Math.Max(0m, request.ValueEur ?? 0m), ParseEur(valuedEur) ?? 0m) : 0m, clock.GetUtcNow(), ct);
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var old = now - CapDefaults.Retention;
        // A reservation (soft or safe) is released when its preview expires: its step's group window has passed, or the step is gone.
        var released = await db.SoftCapSpends
            .Where(x => x.Status == SoftCapStatus.Reserved
                        && (x.ReservedAt < old || !db.ActionSteps.Any(s => s.Id == x.Id && s.ExpiresAt > now)))
            .ExecuteDeleteAsync(ct);
        // T-061: final own-funds rows stay for the no-device daily window plus one hour.
        var oldFinal = now - CapDefaults.FinalRetention;
        var finals = await db.SoftCapSpends.Where(x => x.Status == SoftCapStatus.Final && x.SpentAt < oldFinal).ExecuteDeleteAsync(ct);
        var safes = await db.SafeApprovalCounts.Where(x => x.ApprovedAt < old).ExecuteDeleteAsync(ct);
        return released + finals + safes;
    }

    private static decimal? ParseEur(string? s) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private Task<int> InsertReservedAsync(Guid id, string sub, string tenantId, decimal value, string source, DateTimeOffset at, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SoftCapSpends" ("Id", "OidcSub", "TenantId", "Status", "ReservedAt", "SpentAt", "AmountEur", "ValueSource")
            VALUES ({id}, {sub}, {tenantId}, {nameof(SoftCapStatus.Reserved)}, {at}, NULL, {value}, {source})
            """, ct);

    private Task<int> InsertSafeAsync(Guid id, string sub, string tenantId, SafeApprovalKind kind, decimal value, DateTimeOffset at, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SafeApprovalCounts" ("Id", "OidcSub", "TenantId", "Kind", "ValueEur", "ApprovedAt")
            VALUES ({id}, {sub}, {tenantId}, {kind.ToString()}, {value}, {at})
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
}
