using System.IO.Pipelines;
using System.Text.Json;
using Biatec.Chat.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Biatec.Chat.Tests.Mcp;

/// <summary>A real MCP server (SDK) wired over in-memory pipes; the client under test uses the real SDK client against it.</summary>
public sealed class FakeMcpServer
{
    public List<string> Calls { get; } = [];
    public List<string> Bearers { get; } = [];
    public int ListCount;
    public Func<string, IReadOnlyList<string>> ToolsForBearer { get; set; } = _ => McpToolNames.Required.Append(McpToolNames.SignTransaction).ToList();

    public Func<string, IClientTransport> Transport => bearer => new FakeTransport(this, bearer);

    private sealed class FakeTransport(FakeMcpServer owner, string bearer) : IClientTransport
    {
        public string Name => "fake";

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            owner.Bearers.Add(bearer);
            var c2s = new Pipe();
            var s2c = new Pipe();
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "fake", Version = "1" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) =>
                    {
                        Interlocked.Increment(ref owner.ListCount);
                        var names = owner.ToolsForBearer(bearer);
                        return ValueTask.FromResult(new ListToolsResult
                        {
                            Tools = names.Select(n => new Tool { Name = n, Description = n, InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone() }).ToList(),
                        });
                    },
                    CallToolHandler = (ctx, _) =>
                    {
                        var args = ctx.Params?.Arguments is { } a ? JsonSerializer.Serialize(a) : "{}";
                        owner.Calls.Add(ctx.Params!.Name);
                        return ValueTask.FromResult(new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { tool = ctx.Params.Name, args = JsonDocument.Parse(args).RootElement }) }],
                        });
                    },
                },
            };
            var server = McpServer.Create(new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream()), options);
            _ = server.RunAsync(CancellationToken.None);
            return await new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()).ConnectAsync(cancellationToken);
        }
    }
}

public sealed class McpTests
{
    private sealed class Bearer(string? token = "tok") : IMcpBearerSource
    {
        public Task<string?> GetBearerAsync(string sessionId, CancellationToken ct) => Task.FromResult(token is null ? null : $"{token}-{sessionId}");
    }

    private sealed class Alerts : IMcpAlertSink
    {
        public List<string> Missing { get; } = [];
        public void RequiredToolMissing(McpCallContext context, string toolName) => Missing.Add(toolName);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (BiatecMcpClient Client, FakeMcpServer Server, Alerts Alerts, Clock Clock) Make(IMcpBearerSource? bearer = null)
    {
        var server = new FakeMcpServer();
        var alerts = new Alerts();
        var clock = new Clock();
        var client = new BiatecMcpClient(new SdkMcpConnectionFactory(server.Transport), bearer ?? new Bearer(), new McpToolCache(clock), alerts,
            Options.Create(new McpOptions()), NullLogger<BiatecMcpClient>.Instance);
        return (client, server, alerts, clock);
    }

    private static McpCallContext Ctx(params string[] scopes) => new("t1", "s1", scopes);

    [Fact]
    public async Task Mcp_wrappers_call_the_expected_tools_with_bearer()
    {
        var (c, s, _, _) = Make();
        var ctx = Ctx("openid");
        await c.ListAlgorandAddressesAsync(ctx);
        await c.GetCryptoAddressAsync(ctx, "algorand");
        var bal = await c.GetCryptoBalanceAsync(ctx, "algorand", "ADDR");
        await c.CreatePaymentTransactionAsync(ctx, "algorand", "A", "B", "1.5", 31566704);
        await c.CreateOptInTransactionAsync(ctx, "algorand", "A", 31566704);
        await c.CreateBitcoinTransactionAsync(ctx, "bitcoin", "A", "B", "0.01");
        await c.CreateSwapTransactionAsync(ctx, "algorand", "A", 0, 31566704, "10");
        await c.SubmitTransactionToBlockchainAsync(ctx, "algorand", "AAAA");

        Assert.Equal(["listAlgorandAddresses", "getCryptoAddress", "getCryptoBalance", "createPaymentTransaction",
            "createOptInTransaction", "createBitcoinTransaction", "createSwapTransaction", "submitTransactionToBlockchain"], s.Calls);
        Assert.All(s.Bearers, b => Assert.Equal("tok-s1", b));
        Assert.Equal("ADDR", bal.Json!.Value.GetProperty("args").GetProperty("address").GetString());
    }

    [Fact]
    public async Task Mcp_tool_list_is_cached_and_refreshed_piggyback_after_an_hour()
    {
        var (c, s, _, clock) = Make();
        var ctx = Ctx("openid");
        await c.ListAlgorandAddressesAsync(ctx);
        await c.ListAlgorandAddressesAsync(ctx);
        Assert.Equal(1, s.ListCount);
        clock.Now = clock.Now.AddMinutes(61);
        await c.ListAlgorandAddressesAsync(ctx);
        Assert.Equal(2, s.ListCount);
    }

    [Fact]
    public async Task Mcp_openid_only_cache_is_never_served_to_a_sign_scoped_session()
    {
        var (c, s, _, _) = Make();
        // The server offers fewer tools to an openid-only bearer.
        s.ToolsForBearer = b => McpToolNames.Required.Where(n => n != McpToolNames.SubmitTransactionToBlockchain).ToList();
        var openid = Ctx("openid");
        await c.GetToolsAsync(openid);
        s.ToolsForBearer = _ => McpToolNames.Required;
        var sign = new McpCallContext("t1", "s1", ["sign", "openid"]);
        var tools = await c.GetToolsAsync(sign);
        Assert.Equal(2, s.ListCount);
        Assert.Contains(tools, t => t.Name == McpToolNames.SubmitTransactionToBlockchain);
        Assert.DoesNotContain(await c.GetToolsAsync(openid), t => t.Name == McpToolNames.SubmitTransactionToBlockchain);
        Assert.Equal(2, s.ListCount);
        // Scope order does not matter, tenants do.
        await c.GetToolsAsync(new McpCallContext("t1", "s1", ["openid", "sign"]));
        Assert.Equal(2, s.ListCount);
        await c.GetToolsAsync(new McpCallContext("t2", "s1", ["openid"]));
        Assert.Equal(3, s.ListCount);
    }

    [Fact]
    public async Task Mcp_missing_required_tool_raises_alert_and_fails_the_call()
    {
        var (c, s, alerts, _) = Make();
        s.ToolsForBearer = _ => McpToolNames.Required.Where(n => n != McpToolNames.CreateSwapTransaction).ToList();
        var ex = await Assert.ThrowsAsync<McpToolUnavailableException>(() => c.CreateSwapTransactionAsync(Ctx("openid"), "algorand", "A", 0, 1, "1"));
        Assert.Equal(McpToolNames.CreateSwapTransaction, ex.Tool);
        Assert.Equal([McpToolNames.CreateSwapTransaction], alerts.Missing);
        Assert.Empty(s.Calls);
    }

    [Fact]
    public async Task Mcp_signTransaction_is_never_listed_or_callable()
    {
        var (c, s, _, _) = Make();
        var tools = await c.GetToolsAsync(Ctx("openid", "sign"));
        Assert.DoesNotContain(tools, t => t.Name == "signTransaction");
        Assert.True(McpToolNames.IsBlocked("signTransaction"));
        Assert.True(McpToolNames.IsBlocked(" SIGNTRANSACTION "));
        Assert.DoesNotContain(typeof(IBiatecMcpClient).GetMethods(), m => m.Name.Contains("Sign", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("signTransaction", s.Calls);
        // No public generic call-by-name exists on the client.
        Assert.DoesNotContain(typeof(BiatecMcpClient).GetMethods(), m => m.Name == "CallAsync" || m.Name == "CallToolAsync");
    }

    [Fact]
    public async Task Mcp_without_a_bearer_fails_without_connecting()
    {
        var (c, s, _, _) = Make(new Bearer(null));
        await Assert.ThrowsAsync<McpAuthenticationException>(() => c.ListAlgorandAddressesAsync(Ctx("openid")));
        Assert.Empty(s.Bearers);
    }
}
