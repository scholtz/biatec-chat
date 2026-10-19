using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biatec.Chat.Core.Canonical;

/// <summary>
/// Thrown when a value cannot be canonicalized safely, for example an integer number
/// that is not exactly representable as an IEEE-754 double.
/// </summary>
public sealed class CanonicalJsonException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public CanonicalJsonException(string message) : base(message) { }
}

/// <summary>
/// JSON Canonicalization Scheme (RFC 8785): UTF-8 output, no insignificant whitespace, object members
/// sorted by UTF-16 code units, ECMAScript number formatting and minimal string escaping.
/// Integers whose magnitude exceeds 2^53-1 must be passed as decimal strings; passing them as JSON
/// integer numbers throws <see cref="CanonicalJsonException"/>.
/// </summary>
public static class JsonCanonicalizer
{
    /// <summary>Largest integer magnitude that a JSON number may carry (2^53 - 1).</summary>
    public const long MaxSafeInteger = 9007199254740991L;

    /// <summary>Canonicalizes a JSON node (null means JSON null) and returns the canonical text.</summary>
    /// <exception cref="CanonicalJsonException">A raw integer number exceeds <see cref="MaxSafeInteger"/> or a number is not finite.</exception>
    public static string Canonicalize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>Serializes any object with System.Text.Json and canonicalizes the result.</summary>
    public static string Canonicalize(object? value, JsonSerializerOptions? options = null) =>
        Canonicalize(value is JsonNode n ? n : JsonSerializer.SerializeToNode(value, options));

    /// <summary>Parses JSON text and canonicalizes it.</summary>
    public static string CanonicalizeJson(string json) => Canonicalize(JsonNode.Parse(json));

    /// <summary>Canonicalizes and returns the UTF-8 bytes (the input to hashing).</summary>
    public static byte[] CanonicalizeToUtf8(JsonNode? node) => Encoding.UTF8.GetBytes(Canonicalize(node));

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                var props = obj.ToList();
                props.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                sb.Append('{');
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, props[i].Key);
                    sb.Append(':');
                    Write(sb, props[i].Value);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                WriteValue(sb, val);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue val)
    {
        switch (val.GetValueKind())
        {
            case JsonValueKind.True: sb.Append("true"); return;
            case JsonValueKind.False: sb.Append("false"); return;
            case JsonValueKind.Null: sb.Append("null"); return;
            case JsonValueKind.String:
                // Non-string CLR values that serialize as strings (DateTime, Guid) go through the serializer.
                WriteString(sb, val.TryGetValue<string>(out var str)
                    ? str
                    : JsonSerializer.Deserialize<string>(val.ToJsonString())!);
                return;
        }

        // Number: ToJsonString preserves the original token for parsed documents.
        var raw = val.ToJsonString();
        var isIntegerToken = raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;
        if (isIntegerToken)
        {
            var digits = raw.TrimStart('-');
            var tooBig = digits.Length > 16 ||
                (digits.Length == 16 && string.CompareOrdinal(digits, "9007199254740991") > 0);
            if (tooBig)
                throw new CanonicalJsonException(
                    $"Integer {raw} exceeds 2^53-1; encode it as a decimal string instead.");
        }
        var d = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        sb.Append(FormatNumber(d));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Formats a double exactly like ECMAScript <c>Number.prototype.toString()</c> (RFC 8785 section 3.2.2.3).
    /// Negative zero becomes "0". Throws for NaN and infinities.
    /// </summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new CanonicalJsonException("NaN and Infinity cannot be represented in JSON.");
        if (value == 0) return "0";

        // "R" is the shortest round-trip form; normalize it to digits and exponent.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var (digits, n) = ParseDigits(shortest); // value = 0.digits * 10^n
        var k = digits.Length;
        var sb = new StringBuilder();
        if (value < 0) sb.Append('-');
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static (string digits, int n) ParseDigits(string s)
    {
        var exp = 0;
        var ei = s.IndexOfAny(['E', 'e']);
        if (ei >= 0)
        {
            exp = int.Parse(s[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..ei];
        }
        var dot = s.IndexOf('.');
        var intPart = dot < 0 ? s : s[..dot];
        var frac = dot < 0 ? "" : s[(dot + 1)..];
        var all = intPart + frac;
        var n = intPart.Length + exp;
        var lead = all.Length - all.TrimStart('0').Length;
        all = all.TrimStart('0');
        n -= lead;
        all = all.TrimEnd('0');
        return (all, n);
    }
}
