using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Tools.Caps;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Admin;

/// <summary>
/// Operator change to a tenant's policy. Every field is optional; a missing (null) field is left as it is. Unknown
/// fields are refused, so a request can never touch identity or secret fields (host, oidcClientId, clientSecretRef,
/// allowedOrigins), which only onboarding sets.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantPolicyPatch
{
    public decimal? SoftCapEurPerHour { get; init; }
    /// <summary>The operator attests that the tenant's OIDC client chains to a recognized trust anchor.</summary>
    public bool? TrustAnchorVerified { get; init; }
    public bool? LimitsUpdateEnabled { get; init; }
    public bool? X402Enabled { get; init; }
    /// <summary>Written to <c>bedrockBudgets.usdPerWindow</c>.</summary>
    public decimal? BedrockUsdPerWindow { get; init; }
    public SafeCapsPatch? SafeApprovalCaps { get; init; }
    /// <summary>An eu.anthropic.claude-*-4-* inference profile id; the empty string clears it.</summary>
    public string? DefaultModel { get; init; }
    /// <summary>Why the change is made. Required; it goes into the audit log.</summary>
    public string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafeCapsPatch
{
    public int? ApprovalsPerHour { get; init; }
    public decimal? ApprovalEurPerHour { get; init; }
    public int? ExecutionsPerHour { get; init; }
}

/// <summary>What the admin API shows of a tenant: policy only, never the client secret reference or any secret.</summary>
public sealed record TenantPolicyView(
    string TenantId,
    string Host,
    decimal SoftCapEurPerHour,
    decimal EffectiveSoftCapEurPerHour,
    decimal ConsentThresholdCeilingEur,
    bool TrustAnchorVerified,
    bool LimitsUpdateEnabled,
    bool X402Enabled,
    decimal? BedrockUsdPerWindow,
    SafeCaps SafeApprovalCaps,
    string? DefaultModel)
{
    public static TenantPolicyView Of(Tenant t) => new(
        t.Id, t.Host, t.SoftCapEurPerHour, SoftCapPolicy.EffectiveCapEur(t), t.ConsentThresholdCeilingEur,
        t.TrustAnchorVerified, t.LimitsUpdateEnabled, t.X402Enabled, StoredBudget(t.BedrockBudgets), SafeCaps.Parse(t.SafeApprovalCaps), t.DefaultModel);

    /// <summary>The stored <c>bedrockBudgets.usdPerWindow</c>, or null when the deployment default applies.</summary>
    public static decimal? StoredBudget(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject o && o["usdPerWindow"] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;
        }
        catch (JsonException) { return null; }
    }
}

public enum PolicyStatus { Ok, NotFound, Invalid }

public sealed record PolicyResult(PolicyStatus Status, TenantPolicyView? View = null, string? Error = null, IReadOnlyList<string>? Changes = null);

/// <summary>
/// Reads and changes tenant policy for operators. Changes go through the same save rule as onboarding
/// (<see cref="SoftCapPolicy.ValidateOnSave"/>, here with <c>byOperator: true</c>): the soft cap never drops below the
/// consent threshold ceiling, and it rises above the platform default only for a trust-anchored tenant.
/// </summary>
public sealed partial class TenantPolicyService(ChatDbContext db, Biatec.Chat.Api.Tenancy.ITenantStore tenants, IEvidenceLog evidence)
{
    public const decimal MaxEur = 1_000_000m;
    public const decimal MaxUsdPerWindow = 10_000m;
    public const int MaxCount = 10_000;

    public async Task<IReadOnlyList<TenantPolicyView>> ListAsync(CancellationToken ct) =>
        (await db.Tenants.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct)).Select(TenantPolicyView.Of).ToList();

    public async Task<TenantPolicyView?> GetAsync(string tenantId, CancellationToken ct) =>
        await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct) is { } t ? TenantPolicyView.Of(t) : null;

    /// <summary>A sanitized reason, or null when it is missing or unusable.</summary>
    public static string? CleanReason(string? reason)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r) || r.Length > 200 || r.Any(char.IsControl)) return null;
        return r;
    }

    /// <summary>
    /// Applies the patch. A change commits together with its <see cref="AdminEvidence"/> entry (operator, reason, before
    /// and after value of each changed field), or not at all.
    /// </summary>
    public async Task<PolicyResult> UpdateAsync(string tenantId, TenantPolicyPatch? patch, OperatorIdentity op, CancellationToken ct)
    {
        if (patch is null) return new(PolicyStatus.Invalid, Error: "A JSON body is required.");
        if (CleanReason(patch.Reason) is null) return new(PolicyStatus.Invalid, Error: "reason is required: 1-200 characters, no control characters.");
        if (Check(patch) is { } bad) return new(PolicyStatus.Invalid, Error: bad);

        // One change per tenant at a time, so two operators never overwrite each other's read-modify-write.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"biatec-chat-admin:tenant:" + tenantId}, 0))", ct);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return new(PolicyStatus.NotFound, Error: "Unknown tenant.");

        var changes = new Diff();
        var capRuleInputsChanged = false;
        void Set<T>(string name, T? value, Func<T> get, Action<T> set, bool capRule = false) where T : struct
        {
            if (value is not { } v || EqualityComparer<T>.Default.Equals(v, get())) return;
            changes.Add(name, Show(get()), Show(v));
            set(v);
            capRuleInputsChanged |= capRule;
        }

        Set("softCapEurPerHour", patch.SoftCapEurPerHour, () => tenant.SoftCapEurPerHour, v => tenant.SoftCapEurPerHour = v, capRule: true);
        Set("trustAnchorVerified", patch.TrustAnchorVerified, () => tenant.TrustAnchorVerified, v => tenant.TrustAnchorVerified = v, capRule: true);
        Set("limitsUpdateEnabled", patch.LimitsUpdateEnabled, () => tenant.LimitsUpdateEnabled, v => tenant.LimitsUpdateEnabled = v);
        Set("x402Enabled", patch.X402Enabled, () => tenant.X402Enabled, v => tenant.X402Enabled = v);

        if (patch.BedrockUsdPerWindow is { } usd)
            tenant.BedrockBudgets = Merge(tenant.BedrockBudgets, changes, "bedrockBudgets", ("usdPerWindow", usd, TenantPolicyView.StoredBudget(tenant.BedrockBudgets)));
        if (patch.SafeApprovalCaps is { } caps)
        {
            // Old values are the effective ones (SafeCaps.Parse, which also reads the legacy "perHour" key).
            var current = SafeCaps.Parse(tenant.SafeApprovalCaps);
            tenant.SafeApprovalCaps = Merge(tenant.SafeApprovalCaps, changes, "safeApprovalCaps",
                ("approvalsPerHour", caps.ApprovalsPerHour, current.ApprovalsPerHour),
                ("approvalEurPerHour", caps.ApprovalEurPerHour, current.ApprovalEurPerHour),
                ("executionsPerHour", caps.ExecutionsPerHour, current.ExecutionsPerHour));
        }

        if (patch.DefaultModel is { } model)
        {
            var next = model.Length == 0 ? null : model;
            if (next != tenant.DefaultModel)
            {
                changes.Add("defaultModel", tenant.DefaultModel ?? "(none)", next ?? "(none)");
                tenant.DefaultModel = next;
            }
        }

        // The same save rule as onboarding (only the byOperator flag differs), applied when the patch changes one of
        // its inputs. A patch of other fields, such as switching a feature off, is never blocked by a stored row that
        // predates the rule.
        if (capRuleInputsChanged && SoftCapPolicy.ValidateOnSave(tenant, byOperator: true) is { } capError)
            return new(PolicyStatus.Invalid, Error: capError);

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await AdminEvidence.AppendAsync(evidence, op, AdminEvidence.TenantPolicyUpdate, "tenant:" + tenant.Id, CleanReason(patch.Reason),
                changes.Before, changes.After, ct);
            await tx.CommitAsync(ct);
            // This replica sees the change at once; other replicas within the tenant cache TTL.
            tenants.Invalidate(tenant.Host);
        }
        return new(PolicyStatus.Ok, TenantPolicyView.Of(tenant), Changes: changes.Lines);
    }

    /// <summary>Invariant text for the audit line; decimals without the database's trailing zeros (1500.00 is 1500).</summary>
    private static string Show(object? v) => v switch
    {
        decimal d => d.ToString("0.############", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string? Check(TenantPolicyPatch p)
    {
        if (p.SoftCapEurPerHour is { } cap && (cap <= 0 || cap > MaxEur)) return $"softCapEurPerHour must be between 0 and {MaxEur} EUR.";
        if (p.BedrockUsdPerWindow is { } usd && (usd < 0 || usd > MaxUsdPerWindow)) return $"bedrockUsdPerWindow must be between 0 and {MaxUsdPerWindow} USD.";
        if (p.SafeApprovalCaps is { } c)
        {
            if (c.ApprovalsPerHour is < 0 or > MaxCount || c.ExecutionsPerHour is < 0 or > MaxCount)
                return $"safeApprovalCaps counts must be between 0 and {MaxCount}.";
            if (c.ApprovalEurPerHour is { } eur && (eur < 0 || eur > MaxEur)) return $"safeApprovalCaps.approvalEurPerHour must be between 0 and {MaxEur} EUR.";
        }
        if (p.DefaultModel is { Length: > 0 } m && !Biatec.Chat.Api.Model.TenantModels.IsAllowed(m))
            return "defaultModel must be an eu.anthropic.claude-(haiku|sonnet|opus)-4-* inference profile id (Claude 5 isn't available on this account).";
        return null;
    }

    /// <summary>
    /// Sets the given keys on a stored JSON object (other keys kept; an unreadable value starts over). A key is written
    /// only when its new value differs numerically from the current one (<c>null</c>: the default applies).
    /// </summary>
    private static string Merge(string? json, Diff changes, string name, params (string Key, decimal? New, decimal? Old)[] values)
    {
        JsonObject obj;
        try { obj = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? []; }
        catch (JsonException) { obj = []; }
        foreach (var (key, value, old) in values)
        {
            if (value is not { } v || old == v) continue;
            changes.Add($"{name}.{key}", old is { } o ? Show(o) : "(default)", Show(v));
            obj[key] = JsonValue.Create(v);
        }
        return obj.ToJsonString();
    }

    /// <summary>The changed fields: audit lines (<c>name: old -> new</c>) and the before/after objects of the evidence entry.</summary>
    private sealed class Diff
    {
        public List<string> Lines { get; } = [];
        public JsonObject Before { get; } = [];
        public JsonObject After { get; } = [];
        public int Count => Lines.Count;

        public void Add(string name, string before, string after)
        {
            Lines.Add($"{name}: {before} -> {after}");
            Before[name] = before;
            After[name] = after;
        }
    }
}
