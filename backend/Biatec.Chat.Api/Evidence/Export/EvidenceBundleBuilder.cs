using System.Text.Json.Nodes;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;

namespace Biatec.Chat.Api.Evidence.Export;

/// <summary>
/// The signed evidence export bundle (PRODUCT-DESCRIPTION.md, Evidence Record). Format "biatec-evidence-bundle" v1:
/// <c>{format, version, conversationId, exportedAt, kid, jwksUrl, keys, entries, signature}</c>. <c>keys</c> are the
/// published JWKS entries (a convenience: a verifier must compare them with the real JWKS). The bundle signature is
/// ES256 by <c>kid</c> over the UTF-8 JCS of the bundle without its <c>signature</c> member. Deleted payloads export as
/// <c>payload: null, salt: null</c> and still verify.
/// </summary>
public static class EvidenceBundleBuilder
{
    public const string Format = "biatec-evidence-bundle";
    public const int Version = 1;

    public static async Task<JsonObject> BuildAsync(Guid conversationId, IReadOnlyList<EvidenceEntry> chain, IEvidenceKeyStore keys,
        string? jwksUrl, DateTimeOffset now, CancellationToken ct)
    {
        var all = await keys.GetPublicKeysAsync(ct);
        var used = chain.Select(e => e.Kid).ToHashSet();
        var kid = await keys.GetActiveKidAsync(ct);
        used.Add(kid);

        var body = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["conversationId"] = conversationId.ToString("D"),
            ["exportedAt"] = EvidenceHasher.FormatSignedAt(now),
            ["kid"] = kid,
            ["jwksUrl"] = jwksUrl,
            ["keys"] = EvidenceJwks.ToJwks(all.Where(k => used.Contains(k.Kid)))["keys"]!.DeepClone(),
            ["entries"] = new JsonArray(chain.Select(ToJson).ToArray()),
        };
        var signature = await keys.SignAsync(kid, JsonCanonicalizer.CanonicalizeToUtf8(body), ct);
        body["signature"] = EvidenceVerifier.Base64Url(signature);
        return body;
    }

    private static JsonNode ToJson(EvidenceEntry e) => new JsonObject
    {
        ["seq"] = e.Seq,
        ["kind"] = e.Kind,
        ["anchorRef"] = e.AnchorRef?.ToString("D"),
        ["payload"] = e.Payload is null ? null : JsonNode.Parse(e.Payload),
        ["salt"] = e.Salt,
        ["payloadHash"] = e.PayloadHash,
        ["prevHash"] = e.PrevHash,
        ["entryHash"] = e.EntryHash,
        ["kid"] = e.Kid,
        ["signedAt"] = EvidenceHasher.FormatSignedAt(e.SignedAt),
        ["signature"] = e.Signature,
    };
}
