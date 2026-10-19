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
/// Stateless DNS challenge: the TXT value is an HMAC of the host under a server key, so nothing is stored for a
/// pending request and only someone who controls the zone can publish it.
/// </summary>
public static class Challenge
{
    public const string RecordPrefix = "_biatec-chat-challenge.";
    public const string ValuePrefix = "biatec-chat-verification=";

    public static string RecordName(string host) => RecordPrefix + host;

    public static string Value(string key, string host)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("biatec-chat-onboarding-v1\n" + host));
        return ValuePrefix + Convert.ToHexString(mac, 0, 20).ToLowerInvariant();
    }

    public static bool Matches(string key, string host, IEnumerable<string> txt)
    {
        var expected = Encoding.UTF8.GetBytes(Value(key, host));
        foreach (var t in txt)
        {
            var got = Encoding.UTF8.GetBytes(t.Trim());
            if (got.Length == expected.Length && CryptographicOperations.FixedTimeEquals(got, expected)) return true;
        }
        return false;
    }
}
