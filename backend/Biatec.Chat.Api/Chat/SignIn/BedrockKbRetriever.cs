using Amazon;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime;
using Biatec.Chat.Tools.Kb;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Chat.SignIn;

/// <summary>Bedrock Knowledge Base settings (section <c>Kb:Bedrock</c>). Unset <see cref="KnowledgeBaseId"/> keeps kb.search local.</summary>
public sealed class BedrockKbOptions
{
    public const string Section = "Kb:Bedrock";

    /// <summary>The Bedrock Knowledge Base to query with <c>Retrieve</c>, e.g. <c>DZZGQITEAS</c>.</summary>
    public string? KnowledgeBaseId { get; set; }

    /// <summary>Region of the knowledge base (default eu-central-1).</summary>
    public string Region { get; set; } = "eu-central-1";

    /// <summary>Chunks returned per search (default 4).</summary>
    public int MaxResults { get; set; } = 4;

    /// <summary>A slower Retrieve falls back to the local search (default 5 s).</summary>
    public int TimeoutSeconds { get; set; } = 5;

    public bool Enabled => !string.IsNullOrWhiteSpace(KnowledgeBaseId);
}

/// <summary>Semantic search over the knowledge base held outside the process.</summary>
public interface IKbRetriever
{
    Task<IReadOnlyList<KbHit>> RetrieveAsync(string query, CancellationToken ct);
}

/// <summary>
/// kb.search backed by Bedrock Knowledge Base <c>Retrieve</c>: chunks only, never a generated answer, so the text
/// still reaches the model as untrusted tool data under our own system prompt, tier gate and guardrail. The page id
/// is the S3 object's file name without extension, which matches the docs/kb file the chunk came from.
/// </summary>
public sealed class BedrockKbRetriever(IAmazonBedrockAgentRuntime client, IOptions<BedrockKbOptions> options, KbSearchService kb) : IKbRetriever
{
    private const int MaxExcerpt = 1500;

    public async Task<IReadOnlyList<KbHit>> RetrieveAsync(string query, CancellationToken ct)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        var resp = await client.RetrieveAsync(new RetrieveRequest
        {
            KnowledgeBaseId = o.KnowledgeBaseId,
            RetrievalQuery = new KnowledgeBaseQuery { Text = query },
            RetrievalConfiguration = new KnowledgeBaseRetrievalConfiguration
            {
                // A managed knowledge base rejects VectorSearchConfiguration.
                ManagedSearchConfiguration = new ManagedSearchConfiguration { NumberOfResults = o.MaxResults },
            },
        }, timeout.Token);

        var titles = kb.Pages.ToDictionary(p => p.Id, p => p.Title, StringComparer.Ordinal);
        var hits = new List<KbHit>();
        foreach (var r in resp.RetrievalResults ?? [])
        {
            var text = r.Content?.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var id = PageId(r.Location?.S3Location?.Uri) ?? "knowledge-base";
            var excerpt = text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "...";
            hits.Add(new KbHit(id, titles.GetValueOrDefault(id, id), excerpt, r.Score ?? 0));
        }
        return hits;
    }

    /// <summary><c>s3://bucket/dir/fees-and-costs.md</c> or <c>https://bucket.s3…/dir/fees-and-costs.md</c> → <c>fees-and-costs</c>.</summary>
    public static string? PageId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var name = uri[(uri.LastIndexOf('/') + 1)..];
        var id = Path.GetFileNameWithoutExtension(name);
        return id.Length > 0 ? id : null;
    }

    public static IAmazonBedrockAgentRuntime CreateClient(BedrockKbOptions o) => new AmazonBedrockAgentRuntimeClient(new AmazonBedrockAgentRuntimeConfig
    {
        RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region),
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = 1,
    });
}
