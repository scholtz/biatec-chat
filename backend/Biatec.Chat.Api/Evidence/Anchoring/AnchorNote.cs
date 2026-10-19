using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Api.Evidence.Anchoring;

/// <summary>
/// The note of an anchor transaction: JCS of <c>{v, root, seq, prev, gapFrom}</c>. <c>prev</c> is the previous anchor's
/// transaction ID (null for the first), <c>gapFrom</c> names the first lost sequence number when ticks were lost
/// before this one (the lost numbers are gapFrom .. seq-1), otherwise null.
/// </summary>
public sealed record AnchorNote(string Root, long Seq, string? Prev, long? GapFrom)
{
    public const int Version = 1;
    /// <summary>Algorand's limit for a transaction note.</summary>
    public const int MaxBytes = 1024;

    public byte[] ToBytes()
    {
        var bytes = JsonCanonicalizer.CanonicalizeToUtf8(new JsonObject
        {
            ["v"] = Version,
            ["root"] = Root,
            ["seq"] = Seq,
            ["prev"] = Prev,
            ["gapFrom"] = GapFrom is { } g ? g.ToString(CultureInfo.InvariantCulture) : null,
        });
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Anchor note exceeds the 1024 byte limit.");
        return bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> note, out AnchorNote? parsed)
    {
        parsed = null;
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(note)) is not JsonObject o) return false;
            if ((int?)o["v"] != Version) return false;
            var root = (string?)o["root"];
            if (root is not { Length: 64 } || !root.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
            var seq = (long?)o["seq"];
            if (seq is null or < 1) return false;
            long? gap = null;
            if (o["gapFrom"] is { } gf)
            {
                if (!long.TryParse((string?)gf, NumberStyles.None, CultureInfo.InvariantCulture, out var g) || g < 1 || g >= seq) return false;
                gap = g;
            }
            parsed = new AnchorNote(root, seq.Value, (string?)o["prev"], gap);
            return true;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
