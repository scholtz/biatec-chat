using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Biatec.Chat.Core.Canonical;

/// <summary>SHA-256 helpers. All product hashes are lowercase hex SHA-256 over RFC 8785 canonical JSON.</summary>
public static class Hashing
{
    /// <summary>SHA-256 of the bytes as lowercase hex (64 characters).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>SHA-256 of the UTF-8 encoding of the text as lowercase hex.</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>SHA-256 over the RFC 8785 canonical form of a JSON node, as lowercase hex.</summary>
    /// <exception cref="CanonicalJsonException">The node contains a raw integer above 2^53-1.</exception>
    public static string CanonicalSha256Hex(JsonNode? node) => Sha256Hex(JsonCanonicalizer.CanonicalizeToUtf8(node));

    /// <summary>SHA-256 over the RFC 8785 canonical form of any serializable object, as lowercase hex.</summary>
    public static string CanonicalSha256Hex(object? value) => Sha256Hex(JsonCanonicalizer.Canonicalize(value));
}
