using System.Text.Json;
using Biatec.Chat.Api.Mcp;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Tools.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Biatec.Chat.Tests.Mcp;

/// <summary>The default transport (streamable HTTP with a bearer header) against a real MCP server hosted on Kestrel.</summary>
public sealed class McpHttpAndRegistrationTests
{
    private static readonly string[] Names = [.. McpToolNames.Required, McpToolNames.SignTransaction];

    private static async Task<(WebApplication App, string Url, List<string?> Auth, List<(string Tool, string Args)> Calls)> HostAsync()
    {
        var auth = new List<string?>();
        var calls = new List<(string, string)>();
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = Names.Select(n => new Tool { Name = n, Description = n, InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone() }).ToList(),
            }))
            .WithCallToolHandler((ctx, _) =>
            {
                lock (calls) calls.Add((ctx.Params!.Name, JsonSerializer.Serialize(ctx.Params.Arguments)));
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] });
            });
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            lock (auth) auth.Add(ctx.Request.Headers.Authorization.ToString());
            await next();
        });
        app.MapMcp("/tools");
        await app.StartAsync();
        return (app, app.Urls.First(), auth, calls);
    }

    private sealed class FixedBearer : IMcpBearerSource
    {
        public Task<string?> GetBearerAsync(string sessionId, CancellationToken ct) => Task.FromResult<string?>("tok-" + sessionId);
    }

    [Fact]
    public async Task Mcp_default_http_transport_sends_the_bearer_and_calls_tools_with_documented_argument_names()
    {
        var (app, url, auth, calls) = await HostAsync();
        await using var _ = app;
        var options = new McpOptions { Endpoint = url + "/tools" };
        var client = new BiatecMcpClient(new SdkMcpConnectionFactory(options), new FixedBearer(), new McpToolCache(),
            new LoggingMcpAlertSink(NullLogger<LoggingMcpAlertSink>.Instance), Options.Create(options), NullLogger<BiatecMcpClient>.Instance);
        var ctx = new McpCallContext("t1", "s1", ["openid", "sign"]);

        var tools = await client.GetToolsAsync(ctx);
        Assert.DoesNotContain(tools, t => t.Name == "signTransaction");
        await client.CreatePaymentTransactionAsync(ctx, "algorand", "A", "B", "1.5", 31566704, "n");
        await client.CreateOptInTransactionAsync(ctx, "algorand", "A", 31566704);
        await client.CreateSwapTransactionAsync(ctx, "algorand", "A", 0, 31566704, "10", "0.5");
        await client.SubmitTransactionToBlockchainAsync(ctx, "algorand", "AAAA");
        await client.GetCryptoBalanceAsync(ctx, "algorand", "A");
        await client.CreateBitcoinTransactionAsync(ctx, "bitcoin", "A", "B", "0.01");

        Assert.NotEmpty(auth);
        Assert.All(auth, a => Assert.Equal("Bearer tok-s1", a));

        // Argument names the Biatec MCP tools take (PRODUCT-DESCRIPTION.md tool executors; no published JSON schema is vendored here).
        var expected = new Dictionary<string, string[]>
        {
            ["createPaymentTransaction"] = ["amount", "assetId", "network", "note", "receiver", "sender"],
            ["createOptInTransaction"] = ["address", "assetId", "network"],
            ["createSwapTransaction"] = ["address", "amount", "fromAssetId", "network", "slippage", "toAssetId"],
            ["submitTransactionToBlockchain"] = ["network", "signedTransaction"],
            ["getCryptoBalance"] = ["address", "network"],
            ["createBitcoinTransaction"] = ["amount", "network", "receiver", "sender"],
        };
        foreach (var (tool, names) in expected)
        {
            var args = calls.Single(c => c.Tool == tool).Args;
            var actual = JsonDocument.Parse(args).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(names, actual);
        }
    }

    [Fact]
    public void Mcp_registration_resolves_the_client_binds_options_and_uses_the_vault_bearer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Endpoint"] = "https://example.test/mcp",
            ["Mcp:CacheTtl"] = "00:10:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITokenVault>(_ => (ITokenVault)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TokenVault)));
        services.AddSingleton<IMcpBearerSource, VaultMcpBearerSource>();
        services.AddBiatecMcp(config);
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        var o = sp.GetRequiredService<IOptions<McpOptions>>().Value;
        Assert.Equal("https://example.test/mcp", o.Endpoint);
        Assert.Equal(TimeSpan.FromMinutes(10), o.CacheTtl);
        Assert.Same(sp.GetRequiredService<McpToolCache>(), sp.GetRequiredService<McpToolCache>());
        Assert.IsType<LoggingMcpAlertSink>(sp.GetRequiredService<IMcpAlertSink>());
        Assert.IsType<BiatecMcpClient>(sp.GetRequiredService<IBiatecMcpClient>());
    }

    [Fact]
    public async Task Mcp_cached_list_lacking_a_tool_is_refreshed_once_and_a_reappeared_tool_is_used()
    {
        var server = new FakeMcpServer();
        var missing = new List<string>();
        var alerts = new CountingAlerts(missing);
        var client = new BiatecMcpClient(new SdkMcpConnectionFactory(server.Transport), new FixedBearer(), new McpToolCache(), alerts,
            Options.Create(new McpOptions()), NullLogger<BiatecMcpClient>.Instance);
        var ctx = new McpCallContext("t1", "s1", ["openid"]);

        server.ToolsForBearer = _ => McpToolNames.Required.Where(n => n != McpToolNames.CreateSwapTransaction).ToList();
        await Assert.ThrowsAsync<McpToolUnavailableException>(() => client.CreateSwapTransactionAsync(ctx, "algorand", "A", 0, 1, "1"));
        Assert.Equal(1, server.ListCount);
        // Still missing on the second call: one fresh check and a repeated alert, even though the cache is warm.
        await Assert.ThrowsAsync<McpToolUnavailableException>(() => client.CreateSwapTransactionAsync(ctx, "algorand", "A", 0, 1, "1"));
        Assert.Equal(2, server.ListCount);
        Assert.Equal(2, missing.Count);

        // It reappears: noticed immediately, without waiting for the TTL.
        server.ToolsForBearer = _ => McpToolNames.Required;
        await client.CreateSwapTransactionAsync(ctx, "algorand", "A", 0, 1, "1");
        Assert.Equal(3, server.ListCount);
        Assert.Contains(McpToolNames.CreateSwapTransaction, server.Calls);
    }

    private sealed class CountingAlerts(List<string> sink) : IMcpAlertSink
    {
        public void RequiredToolMissing(McpCallContext context, string toolName) => sink.Add(toolName);
    }
}
