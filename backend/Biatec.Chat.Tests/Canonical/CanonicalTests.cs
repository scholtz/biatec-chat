using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Tests.Canonical;

public class CanonicalTests
{
    [Fact]
    public void Rfc8785_SectionB_Example()
    {
        const string input = "{\"numbers\":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],\"string\":\"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"/\",\"literals\":[null,true,false]}";
        const string expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";
        Assert.Equal(expected, JsonCanonicalizer.CanonicalizeJson(input));
    }

    [Fact]
    public void Rfc8785_Section32_Sorting_Example()
    {
        // Keys sorted by UTF-16 code units, not code points.
        var input = "{\"\\u20ac\":\"Euro Sign\",\"\\r\":\"Carriage Return\",\"\\ufb33\":\"Hebrew Letter Dalet With Dagesh\",\"1\":\"One\",\"\\ud83d\\ude00\":\"Emoji: Grinning Face\",\"\\u0080\":\"Control\\u007f\",\"\\u00f6\":\"Latin Small Letter O With Diaeresis\"}";
        var result = JsonCanonicalizer.CanonicalizeJson(input);
        var keys = JsonNode.Parse(result)!.AsObject().Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "\r", "1", "\u0080", "\u00f6", "\u20ac", "\ud83d\ude00", "\ufb33" }, keys);
        Assert.StartsWith("{\"\\r\":\"Carriage Return\",\"1\":\"One\"", result);
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(-5e-324, "-5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(9007199254740992.0, "9007199254740992")]
    [InlineData(-9007199254740992.0, "-9007199254740992")]
    [InlineData(295147905179352830000.0, "295147905179352830000")]
    [InlineData(9.999999999999997e22, "9.999999999999997e+22")]
    [InlineData(1e23, "1e+23")]
    [InlineData(999999999999999700000.0, "999999999999999700000")]
    [InlineData(999999999999999900000.0, "999999999999999900000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e-6, "0.000001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(4.5, "4.5")]
    [InlineData(123.456, "123.456")]
    [InlineData(-12.0, "-12")]
    [InlineData(2e-3, "0.002")]
    public void NumberFormatting_Rfc8785_AppendixB(double value, string expected) =>
        Assert.Equal(expected, JsonCanonicalizer.FormatNumber(value));

    [Fact]
    public void NegativeZero_IsZero() => Assert.Equal("0", JsonCanonicalizer.FormatNumber(double.NegativeZero));

    [Fact]
    public void NaN_Throws() => Assert.Throws<CanonicalJsonException>(() => JsonCanonicalizer.FormatNumber(double.NaN));

    [Fact]
    public void StringEscaping_Minimal()
    {
        var node = JsonValue.Create("\b\t\n\f\r\"\\\u0001\u001f/\u007f\u00e9\u2028");
        Assert.Equal("\"\\b\\t\\n\\f\\r\\\"\\\\\\u0001\\u001f/\u007f\u00e9\u2028\"", JsonCanonicalizer.Canonicalize(node));
    }

    [Fact]
    public void NestedKeysSorted_NoWhitespace()
    {
        Assert.Equal("{\"a\":{\"x\":1,\"y\":2},\"b\":[{\"c\":1,\"d\":2}]}",
            JsonCanonicalizer.CanonicalizeJson(" { \"b\" : [ { \"d\":2, \"c\":1 } ], \"a\": {\"y\":2,\"x\":1} } "));
    }

    [Fact]
    public void LargeIntegerAsNumber_Throws()
    {
        Assert.Throws<CanonicalJsonException>(() => JsonCanonicalizer.CanonicalizeJson("{\"amount\":9007199254740992}"));
        Assert.Throws<CanonicalJsonException>(() => JsonCanonicalizer.CanonicalizeJson("{\"amount\":-18446744073709551615}"));
        Assert.Throws<CanonicalJsonException>(() => JsonCanonicalizer.Canonicalize(new { amount = 9007199254740993L }));
        var obj = new JsonObject { ["amount"] = 18446744073709551615UL };
        Assert.Throws<CanonicalJsonException>(() => JsonCanonicalizer.Canonicalize(obj));
    }

    [Fact]
    public void LargeIntegerAsString_Passes()
    {
        Assert.Equal("{\"amount\":\"18446744073709551615\"}",
            JsonCanonicalizer.CanonicalizeJson("{\"amount\":\"18446744073709551615\"}"));
    }

    [Fact]
    public void SafeIntegerBoundary_Passes()
    {
        Assert.Equal("[9007199254740991,-9007199254740991]", JsonCanonicalizer.CanonicalizeJson("[9007199254740991,-9007199254740991]"));
    }

    [Fact]
    public void ObjectOverload_UsesSerializer()
    {
        Assert.Equal("{\"a\":1,\"b\":true}", JsonCanonicalizer.Canonicalize(new { b = true, a = 1 }));
    }

    [Fact]
    public void ClrStringLikeValues_Work()
    {
        var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("{\"g\":\"11111111-2222-3333-4444-555555555555\"}",
            JsonCanonicalizer.Canonicalize(new JsonObject { ["g"] = JsonValue.Create(g) }));
    }

    [Fact]
    public void Sha256Hex_KnownVectors()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hashing.Sha256Hex(""));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hashing.Sha256Hex("abc"));
    }

    [Fact]
    public void CanonicalHash_IsKeyOrderIndependent()
    {
        var a = Hashing.CanonicalSha256Hex(JsonNode.Parse("{\"a\":1,\"b\":2}"));
        var b = Hashing.CanonicalSha256Hex(JsonNode.Parse("{\"b\":2,\"a\":1}"));
        Assert.Equal(a, b);
        Assert.Equal(Hashing.Sha256Hex("{\"a\":1,\"b\":2}"), a);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }
}
