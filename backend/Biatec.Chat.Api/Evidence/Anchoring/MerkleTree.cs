using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;

namespace Biatec.Chat.Api.Evidence.Anchoring;

/// <summary>One sibling on an inclusion path. <see cref="Side"/> is where the sibling sits: "left" or "right".</summary>
public sealed record MerkleSibling(string Hash, string Side);

/// <summary>A conversation's head at snapshot time: the leaf input.</summary>
public sealed record AnchorLeafInput(Guid ConversationId, long AnchoredSeq, string HeadHash);

public sealed record MerkleLeafProof(AnchorLeafInput Leaf, int LeafIndex, IReadOnlyList<MerkleSibling> Siblings);

public sealed record MerkleResult(string Root, IReadOnlyList<MerkleLeafProof> Proofs);

/// <summary>
/// The fixed tree of PRODUCT-DESCRIPTION.md (EvidenceAnchorLeaf): leaves sorted by conversationId (its lowercase "D"
/// string, ordinal), <c>leaf = SHA-256(0x00 || JCS({conversationId, anchoredSeq, headHash}))</c>,
/// <c>node = SHA-256(0x01 || left || right)</c>, an unpaired node is promoted unchanged, never duplicated.
/// </summary>
public static class MerkleTree
{
    public static string ConversationKey(Guid id) => id.ToString("D");

    public static byte[] LeafHash(AnchorLeafInput leaf) => LeafHash(leaf.ConversationId, leaf.AnchoredSeq, leaf.HeadHash);

    public static byte[] LeafHash(Guid conversationId, long anchoredSeq, string headHash)
    {
        var json = JsonCanonicalizer.CanonicalizeToUtf8(new JsonObject
        {
            ["conversationId"] = ConversationKey(conversationId),
            ["anchoredSeq"] = anchoredSeq,
            ["headHash"] = headHash,
        });
        return Sha256(0x00, json);
    }

    public static byte[] NodeHash(byte[] left, byte[] right) => Sha256(0x01, left, right);

    private static byte[] Sha256(byte prefix, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData([prefix]);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    /// <summary>Builds the tree. At most one leaf per conversation. Returns the root and one proof per leaf.</summary>
    public static MerkleResult Build(IEnumerable<AnchorLeafInput> inputs)
    {
        var leaves = inputs.OrderBy(l => ConversationKey(l.ConversationId), StringComparer.Ordinal).ToList();
        if (leaves.Count == 0) throw new ArgumentException("A Merkle tree needs at least one leaf.", nameof(inputs));
        if (leaves.Select(l => l.ConversationId).Distinct().Count() != leaves.Count)
            throw new ArgumentException("Duplicate conversation in one anchor.", nameof(inputs));

        var level = leaves.Select(LeafHash).ToList();
        var paths = leaves.Select(_ => new List<MerkleSibling>()).ToList();
        // position[i] = index of leaf i's ancestor in the current level.
        var position = Enumerable.Range(0, leaves.Count).ToArray();

        while (level.Count > 1)
        {
            for (var i = 0; i < leaves.Count; i++)
            {
                var p = position[i];
                var sib = p ^ 1;
                if (sib < level.Count)
                    paths[i].Add(new MerkleSibling(Convert.ToHexStringLower(level[sib]), (p & 1) == 1 ? "left" : "right"));
                position[i] = p / 2;
            }
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
                next.Add(i + 1 < level.Count ? NodeHash(level[i], level[i + 1]) : level[i]);
            level = next;
        }

        var proofs = leaves.Select((l, i) => new MerkleLeafProof(l, i, paths[i])).ToList();
        return new MerkleResult(Convert.ToHexStringLower(level[0]), proofs);
    }

    /// <summary>Recomputes the root from a leaf and its sibling path.</summary>
    public static string ComputeRoot(Guid conversationId, long anchoredSeq, string headHash, IEnumerable<MerkleSibling> siblings)
    {
        var cur = LeafHash(conversationId, anchoredSeq, headHash);
        foreach (var s in siblings)
        {
            var sib = Convert.FromHexString(s.Hash);
            cur = s.Side switch
            {
                "left" => NodeHash(sib, cur),
                "right" => NodeHash(cur, sib),
                _ => throw new FormatException($"Unknown sibling side '{s.Side}'."),
            };
        }
        return Convert.ToHexStringLower(cur);
    }
}
