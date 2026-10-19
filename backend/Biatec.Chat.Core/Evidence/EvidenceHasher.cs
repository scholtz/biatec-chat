using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Core.Evidence;

/// <summary>
/// The fixed evidence encodings (PRODUCT-DESCRIPTION.md, Evidence Record). All hashes are lowercase hex SHA-256 over
/// RFC 8785 canonical JSON of an explicit object. Integers in payloads that may exceed 2^53 must be decimal strings;
/// the canonicalizer rejects raw integers above that.
/// </summary>
public static class EvidenceHasher
{
    /// <summary>prevHash of the first entry of every conversation: 64 zero characters.</summary>
    public static readonly string GenesisPrevHash = new('0', 64);

    /// <summary>A fresh 32-byte random salt as lowercase hex.</summary>
    public static string NewSalt() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256(JCS({salt, payload})).</summary>
    public static string PayloadHash(string saltHex, JsonNode? payload) =>
        Hashing.CanonicalSha256Hex(new JsonObject { ["salt"] = saltHex, ["payload"] = payload?.DeepClone() });

    /// <summary>SHA-256(JCS({conversationId, seq, prevHash, kind, payloadHash, anchorRef})). anchorRef may be null.</summary>
    public static string EntryHash(Guid conversationId, long seq, string prevHash, string kind, string payloadHash, string? anchorRef) =>
        Hashing.CanonicalSha256Hex(new JsonObject
        {
            ["conversationId"] = conversationId.ToString("D"),
            ["seq"] = seq,
            ["prevHash"] = prevHash,
            ["kind"] = kind,
            ["payloadHash"] = payloadHash,
            ["anchorRef"] = anchorRef,
        });

    /// <summary>RFC 3339 UTC with millisecond precision, for example 2026-10-19T12:00:00.000Z.</summary>
    public static string FormatSignedAt(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Truncates to the millisecond so the stored value round-trips through the signed text.</summary>
    public static DateTimeOffset TruncateToMillis(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>The bytes the server signs: UTF-8 of JCS({entryHash, kid, signedAt}).</summary>
    public static byte[] SigningInput(string entryHash, string kid, DateTimeOffset signedAt) =>
        JsonCanonicalizer.CanonicalizeToUtf8(new JsonObject
        {
            ["entryHash"] = entryHash,
            ["kid"] = kid,
            ["signedAt"] = FormatSignedAt(signedAt),
        });
}
