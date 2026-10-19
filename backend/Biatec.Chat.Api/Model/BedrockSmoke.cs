using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Model;

/// <summary>
/// Manual check against real Bedrock, without starting the web host:
/// <c>AWS_PROFILE=hackathon dotnet run --project backend/Biatec.Chat.Api -- bedrock-smoke [--authenticated] [prompt]</c>.
/// It sends the request twice so the second call shows cache reads, and spends a few cents at most.
/// </summary>
public static class BedrockSmoke
{
    public const string Command = "bedrock-smoke";

    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var o = config.GetSection(BedrockOptions.Section).Get<BedrockOptions>() ?? new BedrockOptions();
        o.MaxTokens = Math.Min(o.MaxTokens, 200);

        var profile = args.Contains("--authenticated") ? ModelProfile.Authenticated : ModelProfile.Anonymous;
        var prompt = string.Join(' ', args.Where(a => a != "--authenticated"));
        if (prompt.Length == 0) prompt = "In two sentences, what can you help me with?";

        var client = new BedrockModelClient(
            new BedrockConverseStreamSource(ModelRegistration.CreateBedrockRuntime(o)),
            Options.Create(o), NullLogger<BedrockModelClient>.Instance);
        var request = new ModelRequest
        {
            Profile = profile,
            // Long enough to pass the model's minimum cacheable prefix, so the second call reports cache reads.
            Persona = "You are the Biatec assistant for the Algorand DeFi platform. Answer briefly. " +
                      string.Concat(Enumerable.Repeat("Never sign, move funds or change limits without an explicit user confirmation. ", 120)),
            Catalogue = "Capabilities in this tier: explain.platform, explain.dex, requestSignIn.",
            Messages = [new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = prompt }] }],
        };

        for (var round = 1; round <= 2; round++)
        {
            Console.WriteLine($"--- call {round}");
            await foreach (var ev in client.StreamAsync(request))
            {
                switch (ev)
                {
                    case ModelTextDelta t: Console.Write(t.Text); break;
                    case ModelMessageStop s: Console.WriteLine($"\n[stop: {s.StopReason}]"); break;
                    case ModelUsage u:
                        Console.WriteLine($"[{u.ModelId}: in {u.InputTokens}, out {u.OutputTokens}, cache read {u.CacheReadInputTokens}, cache write {u.CacheWriteInputTokens}, {u.LatencyMs} ms]");
                        break;
                }
            }
        }
        return 0;
    }
}
