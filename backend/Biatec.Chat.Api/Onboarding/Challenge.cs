using System.Security.Cryptography;
using System.Text;

namespace Biatec.Chat.Api.Onboarding;

public sealed class OnboardingOptions
{
    public const string Section = "Onboarding";
    public const int MinKeyLength = 32;

    /// <summary>Secret that derives the per-host DNS challenge values. Onboarding is disabled (503) while unset.</summary>
    public string? ChallengeKey { get; set; }

    /// <summary>Bearer key for the operator approval endpoint. The endpoint is disabled (404) while unset.</summary>
    public string? OperatorKey { get; set; }

    /// <summary>Requests per window and remote IP on the /onboarding endpoints (default 20).</summary>
    public int RateLimitPermits { get; set; } = 20;

    /// <summary>Length of the rate-limit window in seconds (default 60).</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>Largest accepted request body in bytes (default 32 KiB).</summary>
    public int MaxBodyBytes { get; set; } = 32 * 1024;
}

/// <summary>
/// Stateless DNS challenge: the TXT value is an HMAC of the proof host and the requested configuration under a server
/// key, so nothing is stored for a pending request, only someone who controls the zone can publish it, and a value the
/// owner published for one configuration cannot be replayed by a third party to register a different one.
/// The value embeds a 7-day epoch bucket; the current and the previous bucket are accepted, so a published record is
/// honoured for 7 to 14 days and then stops working. It is still not bound to the requester: whoever sees the public
/// TXT value can submit the same configuration within that window, which is harmless because the tenant that results
/// is exactly the one the zone owner asked for.
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

    public const int BucketDays = 7;

    public static long CurrentBucket(TimeProvider clock) => clock.GetUtcNow().ToUnixTimeSeconds() / (BucketDays * 86400L);

    public static string Value(string key, string proofHost, string configDigest, long bucket)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("biatec-chat-onboarding-v3\n" + bucket + "\n" + proofHost + "\n" + configDigest));
        return ValuePrefix + Convert.ToHexString(mac, 0, 20).ToLowerInvariant();
    }

    /// <summary>True when a TXT value matches the current or the previous bucket.</summary>
    public static bool Matches(string key, string host, string configDigest, IEnumerable<string> txt, long currentBucket)
    {
        var expected = new[] { currentBucket, currentBucket - 1 }.Select(b => Encoding.UTF8.GetBytes(Value(key, host, configDigest, b))).ToArray();
        var matched = false;
        foreach (var t in txt)
        {
            var got = Encoding.UTF8.GetBytes(t.Trim());
            foreach (var e in expected)
                if (got.Length == e.Length && CryptographicOperations.FixedTimeEquals(got, e)) matched = true;
        }
        return matched;
    }
}
