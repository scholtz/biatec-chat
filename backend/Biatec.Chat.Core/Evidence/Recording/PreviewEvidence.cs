using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Evidence;

/// <summary>Evidence payloads for previews and their steps: the hashes the user confirmed, never the unsigned bytes.</summary>
public static class PreviewEvidence
{
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static JsonObject Preview(ActionPreview preview, IEnumerable<ActionStep> steps, Guid? rebuiltFrom = null)
    {
        var o = new JsonObject
        {
            ["previewId"] = preview.Id.ToString(),
            ["capability"] = preview.Capability,
            ["canonicalJson"] = preview.CanonicalJson,
            ["canonicalHash"] = Sha256Hex(preview.CanonicalJson),
            ["predictedValueEur"] = preview.PredictedValueEur?.ToString(CultureInfo.InvariantCulture),
            ["predictedNeedsConsent"] = preview.PredictedNeedsConsent,
            ["expiresAt"] = preview.ExpiresAt.ToString("O"),
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode?)Step(s)).ToArray()),
        };
        if (rebuiltFrom is not null) o["rebuiltFrom"] = rebuiltFrom.Value.ToString();
        return o;
    }

    public static JsonObject Step(ActionStep s) => new()
    {
        ["stepId"] = s.Id.ToString(),
        ["seq"] = s.Seq,
        ["kind"] = s.Kind.ToString(),
        ["groupHash"] = s.SubjectHash,
        ["senderAddress"] = s.SenderAddress,
        ["network"] = s.Network,
        ["firstValid"] = s.FirstValid?.ToString(CultureInfo.InvariantCulture),
        ["lastValid"] = s.LastValid?.ToString(CultureInfo.InvariantCulture),
        ["expiresAt"] = s.ExpiresAt.ToString("O"),
    };

}
