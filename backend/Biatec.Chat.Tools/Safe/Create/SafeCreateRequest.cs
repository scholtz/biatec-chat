using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biatec.Chat.Tools.Safe.Create;

/// <summary>
/// What the user asked for: a safe named <see cref="Name"/> on <see cref="Network"/>, deployed by <see cref="From"/>
/// (one of the user's own addresses), with one signer group of <see cref="Signers"/> (the creator first) and
/// <see cref="Threshold"/> approvals.
/// </summary>
public sealed record SafeCreateRequest(string Network, string From, string Name, IReadOnlyList<string> Signers, int Threshold)
{
    /// <summary>The name of the initial signer group.</summary>
    public const string GroupName = "Signers";

    public string StepNetwork => "algorand-" + Network;

    public bool SameAs(SafeCreateRequest o) =>
        Network == o.Network && From == o.From && Name == o.Name && Threshold == o.Threshold
        && Signers.Count == o.Signers.Count && Signers.ToHashSet(StringComparer.Ordinal).SetEquals(o.Signers);

    public JsonObject ToJson() => new()
    {
        ["network"] = Network,
        ["from"] = From,
        ["name"] = Name,
        ["signers"] = new JsonArray(Signers.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["threshold"] = Threshold,
    };

    /// <summary>The request stored in a safe.create preview's canonical JSON, or null if it has none.</summary>
    public static SafeCreateRequest? FromCanonical(string canonicalJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(canonicalJson);
            if (!doc.RootElement.TryGetProperty("request", out var r) || r.ValueKind != JsonValueKind.Object) return null;
            return new SafeCreateRequest(
                r.GetProperty("network").GetString()!, r.GetProperty("from").GetString()!, r.GetProperty("name").GetString()!,
                r.GetProperty("signers").EnumerateArray().Select(s => s.GetString()!).ToList(),
                r.GetProperty("threshold").GetInt32());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public string Describe() =>
        $"{Threshold.ToString(CultureInfo.InvariantCulture)}-of-{Signers.Count.ToString(CultureInfo.InvariantCulture)} Biatec Safe \"{Name}\"";
}
