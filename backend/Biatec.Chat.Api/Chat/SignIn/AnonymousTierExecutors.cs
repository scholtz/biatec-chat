using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Tools.Kb;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Chat.SignIn;

/// <summary>
/// kb.search: Bedrock Knowledge Base retrieval when <c>Kb:Bedrock:KnowledgeBaseId</c> is set (T-112), plus the best
/// operator-uploaded page match, which only the local index holds; the ranked keyword search over docs/kb and uploaded
/// pages when it is not set, fails or finds nothing. The result is data for the model and is wrapped as untrusted.
/// </summary>
public sealed class KbSearchExecutor(KbSearchService kb, IKbRetriever? retriever = null, ILogger<KbSearchExecutor>? log = null) : IToolExecutor
{
    public string CapabilityName => CapabilityRegistry.KbSearch;

    public string? StatusLabel(JsonNode arguments) => "Searching the knowledge base";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        var query = arguments["query"]?.GetValue<string>() ?? "";
        var hits = await RemoteAsync(query, ct) ?? kb.Search(query);
        if (hits.Count == 0)
            return new ToolExecutionResult("No knowledge base page matched. Say you do not have that information.");

        var sb = new System.Text.StringBuilder("Knowledge base results. Cite the source id when you use them.\n");
        foreach (var h in hits)
            sb.Append("\n[source: ").Append(h.Id).Append("] ").Append(h.Title).Append('\n').Append(h.Excerpt).Append('\n');
        return new ToolExecutionResult(sb.ToString());
    }

    /// <summary>The Bedrock hits plus the top uploaded-page hit, or null to use the local search.</summary>
    private async Task<IReadOnlyList<KbHit>?> RemoteAsync(string query, CancellationToken ct)
    {
        if (retriever is null || string.IsNullOrWhiteSpace(query)) return null;
        IReadOnlyList<KbHit> remote;
        try { remote = await retriever.RetrieveAsync(query, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning("Kb: Bedrock retrieve failed, using the local search: {Error}", ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        if (remote.Count == 0) return null;

        var uploaded = kb.Pages.Where(p => p.Source == KbPage.UploadSource).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var upload = uploaded.Count == 0 ? null : kb.Search(query).FirstOrDefault(h => uploaded.Contains(h.Id));
        return upload is null ? remote : [.. remote, upload];
    }
}

/// <summary>
/// requestSignIn: renders the sign-in card, or on a transport where sign-in is unavailable (the public transport) a
/// card pointing at the tenant's sign-in page. It never touches a wallet and performs no action.
/// </summary>
public sealed class RequestSignInExecutor : IToolExecutor
{
    public string CapabilityName => CapabilityRegistry.RequestSignIn;

    public string? StatusLabel(JsonNode arguments) => "Preparing sign-in";

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        var reason = arguments["reason"]?.GetValue<string>();
        var pageUrl = context.Tenant?.SignInPageUrl;

        // The widget POSTs this endpoint on the tenant's own backend to get the one-time popup URL. Without an
        // absolute origin we cannot build it, so we fall through to the unavailable branch.
        var origin = context.Tenant is { } t ? Biatec.Chat.Api.Hosting.BackendUrls.TryBase(t) : null;

        if (context.SignInAvailable && origin is not null)
        {
            var card = new SignInCardEvent(origin + "/auth/popup", SignInReason.SignIn, null, reason, Unavailable: false);
            return Task.FromResult(new ToolExecutionResult(
                "A sign-in button is now shown to the user. Nothing has been done yet. Explain that signing in is needed and what the action would do in general terms.",
                Events: [card]));
        }

        if (!string.IsNullOrEmpty(pageUrl))
        {
            var card = new SignInCardEvent(pageUrl, SignInReason.SignIn, null,
                "Sign-in is not available on this page. Open the sign-in page and start again signed in.", Unavailable: true);
            return Task.FromResult(new ToolExecutionResult(
                "Sign-in is unavailable on this page, so a link to the sign-in page is shown instead. Tell the user to continue there; nothing has been done.",
                Events: [card]));
        }

        return Task.FromResult(new ToolExecutionResult(
            "Sign-in is unavailable on this page and no sign-in page is configured. Tell the user they need to sign in on a Biatec site first; nothing has been done."));
    }
}

public static class AnonymousTierRegistration
{
    public static IServiceCollection AddAnonymousTier(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<KbOptions>(config.GetSection(KbOptions.Section));
        // Starts with the seed pages; KbLibrary overlays the uploaded ones from PostgreSQL (T-096).
        services.AddSingleton(sp => new KbSearchService(KbLibrary.LoadSeed(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KbOptions>>().Value.ResolvedPath,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<KbLibrary>())));
        services.AddSingleton<KbLibrary>();
        services.AddHostedService<KbRefreshService>();
        // T-112: with Kb:Bedrock:KnowledgeBaseId set, kb.search retrieves from the Bedrock Knowledge Base first.
        var bedrockKb = config.GetSection(BedrockKbOptions.Section);
        services.Configure<BedrockKbOptions>(bedrockKb);
        if (bedrockKb.Get<BedrockKbOptions>() is { Enabled: true } kbo)
        {
            services.TryAddSingleton<Amazon.BedrockAgentRuntime.IAmazonBedrockAgentRuntime>(_ => BedrockKbRetriever.CreateClient(kbo));
            services.TryAddSingleton<IKbRetriever, BedrockKbRetriever>();
        }
        services.AddSingleton<IToolExecutor, KbSearchExecutor>();
        services.AddSingleton<IToolExecutor, RequestSignInExecutor>();
        return services;
    }
}
