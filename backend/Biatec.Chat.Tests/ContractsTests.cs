using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Biatec.Chat.Tests;

public class ContractsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] ExpectedSchemas =
    [
        "SessionState", "SessionTier", "SignInStatus", "Suggestion", "SuggestionList", "ChatMessageRequest",
        "ActionDecisionRequest", "ActionPreview", "ActionStep", "AssetAmount", "ConsentStatus", "ConsentState",
        "ChatEvent", "ChatEventTextDeltaEvent", "ChatEventToolStatusEvent", "ChatEventPreviewCardEvent",
        "ChatEventConsentStatusEvent", "ChatEventSignInCardEvent", "ChatEventErrorEvent",
    ];

    private readonly WebApplicationFactory<Program> _factory;

    public ContractsTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("environment", Environments.Development));

    [Fact]
    public async Task OpenApiDocument_IncludesEveryContract_AndDiscriminatedEventUnion()
    {
        using var client = _factory.CreateClient();
        var doc = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        var schemas = doc["components"]!["schemas"]!.AsObject();

        foreach (var name in ExpectedSchemas)
            Assert.True(schemas.ContainsKey(name), $"Missing schema {name}");

        Assert.Equal("type", (string?)schemas["ChatEvent"]!["discriminator"]!["propertyName"]);
        Assert.Equal(6, schemas["ChatEvent"]!["discriminator"]!["mapping"]!.AsObject().Count);
        foreach (var variant in schemas.Where(s => s.Key.StartsWith("ChatEvent") && s.Key != "ChatEvent"))
            Assert.Contains("type", variant.Value!["required"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void ChatEvent_SerializesWithTypeDiscriminator_AndRoundTrips()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ChatEvent e = new ToolStatusEvent("t1", "Checking your USDC balance", ToolRunState.Started);

        var json = JsonSerializer.Serialize(e, options);
        var node = JsonNode.Parse(json)!;
        Assert.Equal("tool_status", (string?)node["type"]);
        Assert.Equal("Started", (string?)node["state"]);

        var back = JsonSerializer.Deserialize<ChatEvent>(json, options);
        Assert.Equal(e, back);
    }
}
