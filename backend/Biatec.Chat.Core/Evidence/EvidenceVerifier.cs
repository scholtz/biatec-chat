using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Core.Evidence;

public sealed record EvidenceVerification(bool Valid, long? FailedSeq, string? Error)
{
    public static readonly EvidenceVerification Ok = new(true, null, null);
    public static EvidenceVerification Fail(long seq, string error) => new(false, seq, error);
}

/// <summary>Offline verification of one conversation's chain, exactly as an auditor would do it.</summary>
public static class EvidenceVerifier
{
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }

    /// <summary>
    /// Checks seq continuity from 1, the 64-zero genesis prevHash, prevHash linkage, entryHash, payloadHash (only where
    /// payload and salt are present; the stored hash is trusted for deleted payloads) and the signature against the
    /// public keys (by kid). <paramref name="entries"/> must be one conversation, in any order.
    /// </summary>
    public static EvidenceVerification Verify(IEnumerable<EvidenceEntry> entries, IReadOnlyDictionary<string, EvidencePublicKey> keys)
    {
        var prev = EvidenceHasher.GenesisPrevHash;
        long expected = 1;
        Guid? conv = null;
        foreach (var e in entries.OrderBy(x => x.Seq))
        {
            conv ??= e.ConversationId;
            if (e.ConversationId != conv) return EvidenceVerification.Fail(e.Seq, "Entries from more than one conversation.");
            if (e.Seq != expected) return EvidenceVerification.Fail(e.Seq, $"Expected seq {expected}.");
            if (e.PrevHash != prev) return EvidenceVerification.Fail(e.Seq, "prevHash does not match the previous entryHash.");

            if (e.Payload is not null || e.Salt is not null)
            {
                if (e.Payload is null || e.Salt is null) return EvidenceVerification.Fail(e.Seq, "Payload and salt must be present or deleted together.");
                if (EvidenceHasher.PayloadHash(e.Salt, JsonNode.Parse(e.Payload)) != e.PayloadHash)
                    return EvidenceVerification.Fail(e.Seq, "payloadHash does not match the payload.");
            }

            var anchorRef = e.AnchorRef?.ToString("D");
            if (EvidenceHasher.EntryHash(e.ConversationId, e.Seq, e.PrevHash, e.Kind, e.PayloadHash, anchorRef) != e.EntryHash)
                return EvidenceVerification.Fail(e.Seq, "entryHash does not match the entry.");

            if (!keys.TryGetValue(e.Kid, out var key)) return EvidenceVerification.Fail(e.Seq, $"Unknown signing key '{e.Kid}'.");
            bool sigOk;
            try
            {
                using var ec = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = key.X, Y = key.Y },
                });
                sigOk = ec.VerifyData(EvidenceHasher.SigningInput(e.EntryHash, e.Kid, e.SignedAt), FromBase64Url(e.Signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                sigOk = false;
            }
            if (!sigOk) return EvidenceVerification.Fail(e.Seq, "Signature does not verify.");

            prev = e.EntryHash;
            expected++;
        }
        return EvidenceVerification.Ok;
    }
}
