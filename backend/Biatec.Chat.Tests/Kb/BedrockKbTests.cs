using System.Text.Json.Nodes;
using Amazon;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Tools.Kb;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Kb;

/// <summary>T-112: kb.search retrieves from the Bedrock Knowledge Base and falls back to the local keyword search.</summary>
public sealed class BedrockKbTests
{
    private static readonly KbSearchService Local = new([
        new KbPage("fees-and-costs", "Fees and costs", "# Fees and costs\n\nThe minimum Algorand fee is 0.001 ALGO."),
        new KbPage("tenant-hours", "Support hours", "# Support hours\n\nOur desk answers fee questions 9 to 17.", KbPage.UploadSource),
    ]);

    private static ToolExecutionContext Ctx() =>
        new(CapabilityRegistry.CreateDefault().Find("kb.search")!, "tu-1", Guid.NewGuid(), "s", "t", Biatec.Chat.Core.Data.SessionTier.Anonymous, [], null, true);

    private sealed class FakeRetriever(Func<IReadOnlyList<KbHit>> answer) : IKbRetriever
    {
        public int Calls;
        public Task<IReadOnlyList<KbHit>> RetrieveAsync(string query, CancellationToken ct) { Calls++; return Task.FromResult(answer()); }
    }

    private static Task<ToolExecutionResult> Search(IKbRetriever? r, string query, KbSearchService? kb = null) =>
        new KbSearchExecutor(kb ?? Local, r).ExecuteAsync(Ctx(), new JsonObject { ["query"] = query }, default);

    [Fact]
    public async Task Retrieved_chunks_are_cited_by_page_and_the_best_uploaded_page_is_added()
    {
        var r = new FakeRetriever(() => [new KbHit("swaps", "Swapping assets", "Slippage caps the worst price.", 0.8)]);
        var res = await Search(r, "fee for a swap");
        Assert.Equal(1, r.Calls);
        Assert.Contains("[source: swaps] Swapping assets\nSlippage caps the worst price.", res.Content);
        Assert.Contains("[source: tenant-hours]", res.Content); // only the local index holds uploads
        Assert.DoesNotContain("[source: fees-and-costs]", res.Content); // seed pages come from Bedrock
    }

    [Fact]
    public async Task Failing_or_empty_retrieval_falls_back_to_the_local_search()
    {
        var failing = await Search(new FakeRetriever(() => throw new AmazonServiceException("AccessDenied")), "Algorand fee");
        Assert.Contains("[source: fees-and-costs]", failing.Content);

        var empty = await Search(new FakeRetriever(() => []), "Algorand fee");
        Assert.Contains("[source: fees-and-costs]", empty.Content);

        var none = await Search(new FakeRetriever(() => []), "zzzqqq");
        Assert.Contains("No knowledge base page matched", none.Content);
    }

    [Fact]
    public async Task Without_a_knowledge_base_id_the_search_stays_local()
    {
        var res = await Search(null, "Algorand fee");
        Assert.Contains("[source: fees-and-costs]", res.Content);
    }

    private sealed class FakeClient(RetrieveResponse response) : AmazonBedrockAgentRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public RetrieveRequest? Last;
        public override Task<RetrieveResponse> RetrieveAsync(RetrieveRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Retriever_maps_results_to_page_ids_and_local_titles()
    {
        var client = new FakeClient(new RetrieveResponse
        {
            RetrievalResults =
            [
                new KnowledgeBaseRetrievalResult
                {
                    Content = new RetrievalResultContent { Text = "  The minimum fee is 0.001 ALGO. " },
                    Location = new RetrievalResultLocation { S3Location = new RetrievalResultS3Location { Uri = "https://bucket.s3.eu-central-1.amazonaws.com/kb/fees-and-costs.md" } },
                    Score = 0.9,
                },
                new KnowledgeBaseRetrievalResult { Content = new RetrievalResultContent { Text = "" } },
                new KnowledgeBaseRetrievalResult
                {
                    Content = new RetrievalResultContent { Text = new string('x', 2000) },
                    Location = new RetrievalResultLocation { S3Location = new RetrievalResultS3Location { Uri = "s3://bucket/new-page.md" } },
                },
            ],
        });
        var retriever = new BedrockKbRetriever(client, Options.Create(new BedrockKbOptions { KnowledgeBaseId = "DZZGQITEAS", MaxResults = 3 }), Local);

        var hits = await retriever.RetrieveAsync("fees", default);

        Assert.Equal("DZZGQITEAS", client.Last!.KnowledgeBaseId);
        Assert.Equal(3, client.Last.RetrievalConfiguration.ManagedSearchConfiguration.NumberOfResults);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new KbHit("fees-and-costs", "Fees and costs", "The minimum fee is 0.001 ALGO.", 0.9), hits[0]);
        Assert.Equal("new-page", hits[1].Title); // not in the local index: the id is the title
        Assert.EndsWith("...", hits[1].Excerpt);
    }

    [Theory]
    [InlineData("s3://b/kb/glossary.md", "glossary")]
    [InlineData("s3://b/faq", "faq")]
    [InlineData("s3://b/", null)]
    [InlineData(null, null)]
    public void PageId_is_the_file_name_without_extension(string? uri, string? id) =>
        Assert.Equal(id, BedrockKbRetriever.PageId(uri));

    [BedrockKbFact]
    public async Task Live_retrieve_returns_cited_docs_kb_chunks()
    {
        var o = new BedrockKbOptions { KnowledgeBaseId = Environment.GetEnvironmentVariable("BIATEC_TEST_KB_ID") };
        var retriever = new BedrockKbRetriever(BedrockKbRetriever.CreateClient(o), Options.Create(o), KbSearchService.FromDirectory(KbSeed.Dir()));
        var hits = await retriever.RetrieveAsync("What happens if I lose my phone?", default);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Id == "phone-approval-authenticator" && h.Title == "Phone approval with Biatec Authenticator");
    }
}

/// <summary>Runs only with BIATEC_TEST_KB_ID set (and AWS credentials for eu-central-1), after infra/kb/sync.sh.</summary>
public sealed class BedrockKbFactAttribute : FactAttribute
{
    public BedrockKbFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BIATEC_TEST_KB_ID")))
            Skip = "Set BIATEC_TEST_KB_ID (and AWS credentials for eu-central-1) to run the Bedrock Knowledge Base test.";
    }
}

internal static class KbSeed
{
    /// <summary>The repo's docs/kb, found by walking up from the test binary.</summary>
    public static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "kb"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "kb");
    }
}
