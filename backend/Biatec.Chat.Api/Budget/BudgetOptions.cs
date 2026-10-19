using System.ComponentModel.DataAnnotations;

namespace Biatec.Chat.Api.Budget;

/// <summary>
/// Bedrock spend caps, bound from the <c>Budget</c> section. Spend is counted in fixed windows of
/// <see cref="WindowMinutes"/> (UTC), per tenant and across all tenants. A tenant may set its own cap in
/// <c>Tenant.BedrockBudgets</c> as <c>{"usdPerWindow": 5}</c>; without it <see cref="TenantUsdPerWindow"/> applies.
/// </summary>
public sealed class BudgetOptions : IValidatableObject
{
    public const string Section = "Budget";

    public bool Enabled { get; set; } = true;

    [Range(1, 44640)] public int WindowMinutes { get; set; } = 1440;

    [Range(0, 1_000_000)] public decimal GlobalUsdPerWindow { get; set; } = 20m;

    [Range(0, 1_000_000)] public decimal TenantUsdPerWindow { get; set; } = 5m;

    /// <summary>
    /// Share of each budget that only authenticated sessions may use: anonymous sessions are degraded once spend
    /// reaches <c>(1 - share) × budget</c>, so a distributed anonymous client can't starve signed-in users.
    /// </summary>
    [Range(0, 1)] public decimal ReservedAuthenticatedShare { get; set; } = 0.25m;

    /// <summary>How long an anonymous answer stays available as a cached answer while the breaker is open.</summary>
    [Range(1, 10080)] public int CachedAnswerTtlMinutes { get; set; } = 1440;

    /// <summary>Prices per model id; models not listed fall back to <see cref="ModelPrices.Defaults"/>.</summary>
    public List<ModelPrice> Prices { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Prices.Any(p => string.IsNullOrWhiteSpace(p.ModelId)))
            yield return new ValidationResult("Budget:Prices entries need a ModelId.", [nameof(Prices)]);
    }
}

/// <summary>USD per million tokens. A list, not a map, because model ids contain ':' (the configuration separator).</summary>
public sealed class ModelPrice
{
    public string ModelId { get; set; } = "";
    [Range(0, 10_000)] public decimal InputUsdPerMTok { get; set; }
    [Range(0, 10_000)] public decimal OutputUsdPerMTok { get; set; }
    [Range(0, 10_000)] public decimal CacheReadUsdPerMTok { get; set; }
    [Range(0, 10_000)] public decimal CacheWriteUsdPerMTok { get; set; }
}

public static class ModelPrices
{
    /// <summary>EU inference profile prices on this account (cache read 0.1×, cache write 1.25× input).</summary>
    public static readonly IReadOnlyList<ModelPrice> Defaults =
    [
        new() { ModelId = "eu.anthropic.claude-haiku-4-5-20251001-v1:0", InputUsdPerMTok = 1.10m, OutputUsdPerMTok = 5.50m, CacheReadUsdPerMTok = 0.11m, CacheWriteUsdPerMTok = 1.375m },
        new() { ModelId = "eu.anthropic.claude-sonnet-4-5-20250929-v1:0", InputUsdPerMTok = 3.30m, OutputUsdPerMTok = 16.50m, CacheReadUsdPerMTok = 0.33m, CacheWriteUsdPerMTok = 4.125m },
        new() { ModelId = "eu.anthropic.claude-opus-4-5-20251101-v1:0", InputUsdPerMTok = 5.50m, OutputUsdPerMTok = 27.50m, CacheReadUsdPerMTok = 0.55m, CacheWriteUsdPerMTok = 6.875m },
    ];

    /// <summary>An unknown model is charged at the most expensive default, so a missing price never undercounts.</summary>
    public static ModelPrice For(string modelId, IReadOnlyList<ModelPrice> configured) =>
        configured.FirstOrDefault(p => p.ModelId == modelId)
        ?? Defaults.FirstOrDefault(p => p.ModelId == modelId)
        ?? Defaults.MaxBy(p => p.OutputUsdPerMTok)!;

    /// <summary>Cost in micro-USD, rounded up so many small calls never round to zero.</summary>
    public static long MicroUsd(Biatec.Chat.Api.Model.ModelUsage u, ModelPrice p)
    {
        // Bedrock's InputTokens excludes the cached tokens, which are reported separately.
        var usd = (u.InputTokens * p.InputUsdPerMTok + u.OutputTokens * p.OutputUsdPerMTok
            + u.CacheReadInputTokens * p.CacheReadUsdPerMTok + u.CacheWriteInputTokens * p.CacheWriteUsdPerMTok) / 1_000_000m;
        return (long)Math.Ceiling(usd * 1_000_000m);
    }
}
