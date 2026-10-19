using System.Security.Cryptography;
using System.Text;

namespace Biatec.Chat.Api.Onboarding;

public sealed class OnboardingOptions
{
    public const string Section = "Onboarding";

    /// <summary>Secret that derives the per-host DNS challenge values. Onboarding is disabled (503) while unset.</summary>
    public string? ChallengeKey { get; set; }

    /// <summary>Bearer key for the operator approval endpoint. The endpoint is disabled (404) while unset.</summary>
    public string? OperatorKey { get; set; }
}

/// <summary>
/// Stateless DNS challenge: the TXT value is an HMAC of the proof host and the requested configuration under a server
/// key, so nothing is stored for a pending request, only someone who controls the zone can publish it, and a value the
/// owner published for one configuration cannot be replayed by a third party to register a different one.
/// </summary>
public static class Challenge
{
    public const string RecordPrefix = "_biatec-chat-challenge.";
    public const string ValuePrefix = "biatec-chat-verification=";

    public static string RecordName(string host) => RecordPrefix + host;

    /// <summary>Digest of the requested tenant configuration.</summary>
    public static string Digest(string host, string? signIn, IEnumerable<string> origins, IEnumerable<string> clientKeys)
    {
        var lines = new List<string> { "host=" + host, "signIn=" + signIn };
        lines.AddRange(origins.OrderBy(o => o, StringComparer.Ordinal).Select(o => "origin=" + o));
        lines.AddRange(clientKeys.OrderBy(k => k, StringComparer.Ordinal).Select(k => "key=" + k.Length + ":" + k));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    public static string Value(string key, string proofHost, string configDigest)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("biatec-chat-onboarding-v2\n" + proofHost + "\n" + configDigest));
        return ValuePrefix + Convert.ToHexString(mac, 0, 20).ToLowerInvariant();
    }

    public static bool Matches(string key, string host, string configDigest, IEnumerable<string> txt)
    {
        var expected = Encoding.UTF8.GetBytes(Value(key, host, configDigest));
        foreach (var t in txt)
        {
            var got = Encoding.UTF8.GetBytes(t.Trim());
            if (got.Length == expected.Length && CryptographicOperations.FixedTimeEquals(got, expected)) return true;
        }
        return false;
    }
}
