using System.Text.Json;
using System.Text.RegularExpressions;

namespace Biatec.Chat.Api.Model;

/// <summary>
/// The allow-list for a tenant-supplied model id: an <c>eu.</c> Claude 4.x inference profile only (Claude 5 isn't
/// available on this account). Checked when the tenant is saved and again when the id is used.
/// </summary>
public static partial class TenantModels
{
    [GeneratedRegex(@"^eu\.anthropic\.claude-(haiku|sonnet|opus)-4-\d+-[A-Za-z0-9.:-]{1,80}$")]
    private static partial Regex ModelRegex();

    public static bool IsAllowed(string? modelId) => modelId is { Length: > 0 and <= 200 } && ModelRegex().IsMatch(modelId);

    /// <summary>
    /// The tenant's model for a session, or null for the configured one. It applies to authenticated sessions only:
    /// anonymous discovery stays on the cheaper configured model. A stored id that fails the allow-list is ignored.
    /// </summary>
    public static string? For(string? tenantDefaultModel, bool anonymous) => !anonymous && IsAllowed(tenantDefaultModel) ? tenantDefaultModel : null;
}

/// <summary>A tenant's sample prompts: a JSON array of strings in <c>Tenant.Suggestions</c>.</summary>
public static class TenantSuggestions
{
    public const int Max = 4;
    public const int MaxLength = 200;

    /// <summary>The usable prompts (at most four, trimmed, no control characters), or null when none are set.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonSerializer.Deserialize<List<string?>>(json) is not { } all) return null;
            var list = all.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s) && s.Length <= MaxLength && !s.Any(char.IsControl)).Take(Max).Select(s => s!).ToList();
            return list.Count == 0 ? null : list;
        }
        catch (JsonException) { return null; }
    }
}
