using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.X402;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.X402;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Biatec.Chat.Tests.X402;

/// <summary>Needs the docker-compose postgres; each run migrates a fresh, uniquely named database.</summary>
public abstract class X402ExecutorBase
{
    protected static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private sealed class FakeMcp(string[] addresses) : IBiatecMcpClient
    {
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(addresses);
            return Task.FromResult(new McpToolResult(false, json, JsonDocument.Parse(json).RootElement.Clone()));
        }
        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new NotSupportedException();
    }

    protected readonly Guid _conversation = Guid.NewGuid();
    protected readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    protected string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    protected X402PayExecutor Executor(string[] owned, bool tenantFlag, out IServiceProvider sp)
    {
        var options = new X402Options { Enabled = true, AllowedHosts = ["shop.example.com"] };
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(_ => new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options));
        services.AddScoped<ActionPreviewService>();
        var stub = new StubResource(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent(X402Fixtures.Requirements()) });
        services.AddTransient(_ => new X402PayService(new X402HostPolicy(options, new FakeResolver("93.184.216.34")), new HttpClient(stub),
            new FakeBuilders(() => [X402Fixtures.Good]), new MockX402Facilitator(), options));
        sp = services.BuildServiceProvider();
        _ = tenantFlag;
        return new X402PayExecutor(sp.GetRequiredService<IServiceScopeFactory>(), new FakeMcp(owned), TimeProvider.System);
    }

    protected ToolExecutionContext Ctx(bool tenantFlag, Biatec.Chat.Core.Data.SessionTier tier = Biatec.Chat.Core.Data.SessionTier.Authenticated) =>
        new(CapabilityRegistry.CreateDefault().Find("x402.pay")!, "tu1", _conversation, "sess", "t1", tier, ["openid", "sign"],
            new Tenant { Id = "t1", X402Enabled = tenantFlag });

    protected static JsonObject Args(string from = X402Fixtures.From) =>
        new() { ["network"] = "testnet", ["from"] = from, ["resourceUrl"] = "https://shop.example.com/premium" };
}

/// <summary>Needs the docker-compose postgres.</summary>
public sealed class X402ExecutorPreviewTests : X402ExecutorBase, IAsyncLifetime
{
    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);
        await db.Database.MigrateAsync();
        db.Conversations.Add(new Conversation { Id = _conversation, TenantId = "t1", CreatedAt = DateTimeOffset.UtcNow, LastMessageAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    [Fact]
    public async Task X402_executor_preview_shows_the_decoded_transfer_value()
    {
        var exec = Executor([X402Fixtures.From], true, out var sp);
        var result = await exec.ExecuteAsync(Ctx(true), Args(), default);
        Assert.False(result.IsError, result.Content);
        var card = Assert.IsType<PreviewCardEvent>(Assert.Single(result.Events!)).Preview;
        var step = Assert.Single(card.Steps);
        var asset = Assert.Single(step.Assets);
        Assert.Equal("250000", asset.Amount);
        Assert.Equal("USDC", asset.Symbol);
        Assert.Equal(6, asset.Decimals);
        Assert.Equal(X402Fixtures.From, step.From);
        Assert.Equal(X402Fixtures.PayTo, step.To);
        Assert.Contains("0.25 USDC", card.Intent);
        Assert.Equal(64, step.GroupHash.Length);

        // The stored step is bound to the same group hash and carries the unsigned group.
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var stored = await db.ActionSteps.SingleAsync();
        Assert.Equal(step.GroupHash, stored.SubjectHash);
        Assert.Single(JsonNode.Parse(stored.UnsignedGroup!)!.AsArray());
        Assert.Equal("250000", (string?)JsonNode.Parse((await db.ActionPreviews.SingleAsync()).CanonicalJson)!["amount"]);
    }
}

/// <summary>No database: these paths return before anything is stored.</summary>
public sealed class X402ExecutorGuardTests : X402ExecutorBase
{
    [Fact]
    public async Task X402_executor_refuses_an_address_the_user_does_not_own()
    {
        var exec = Executor(["SOMEONEELSE"], true, out _);
        var result = await exec.ExecuteAsync(Ctx(true), Args(), default);
        Assert.True(result.IsError);
        Assert.Null(result.Events);
    }

    [Fact]
    public async Task X402_executor_refuses_when_the_tenant_flag_is_off()
    {
        var exec = Executor([X402Fixtures.From], false, out _);
        var result = await exec.ExecuteAsync(Ctx(false), Args(), default);
        Assert.True(result.IsError);
        Assert.Null(result.Events);
    }

    [Fact]
    public async Task X402_executor_refuses_a_session_that_is_not_authenticated()
    {
        var exec = Executor([X402Fixtures.From], true, out _);
        var result = await exec.ExecuteAsync(Ctx(true, Biatec.Chat.Core.Data.SessionTier.Anonymous), Args(), default);
        Assert.True(result.IsError);
        Assert.Null(result.Events);
    }

    [Theory]
    [InlineData("network", null)]
    [InlineData("network", "net")]
    [InlineData("network", "algorand-testnet")]
    [InlineData("from", null)]
    [InlineData("from", "")]
    [InlineData("resourceUrl", null)]
    [InlineData("resourceUrl", "  ")]
    public async Task X402_executor_returns_a_tool_error_for_bad_arguments_without_any_request(string name, string? value)
    {
        var exec = Executor([X402Fixtures.From], true, out _);
        var args = Args();
        args[name] = value;
        var result = await exec.ExecuteAsync(Ctx(true), args, default);
        Assert.True(result.IsError);
        Assert.Null(result.Events);
    }

    [Fact]
    public async Task X402_executor_returns_a_tool_error_when_an_argument_is_not_a_string()
    {
        var exec = Executor([X402Fixtures.From], true, out _);
        var args = Args();
        args["from"] = 5;
        var result = await exec.ExecuteAsync(Ctx(true), args, default);
        Assert.True(result.IsError);
    }

    [Fact]
    public void X402_expiry_follows_the_round_window_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(100 * 2.8), X402PayExecutor.ExpiryWindow(1000, 1100));
        Assert.Equal(TimeSpan.FromMinutes(30), X402PayExecutor.ExpiryWindow(1000, 1_000_000));
        Assert.Equal(TimeSpan.Zero, X402PayExecutor.ExpiryWindow(1000, 900));
    }

    [Fact]
    public async Task X402_executor_reports_policy_violations_without_a_preview()
    {
        var exec = Executor([X402Fixtures.From], true, out _);
        var args = Args();
        args["resourceUrl"] = "https://169.254.169.254/latest";
        var result = await exec.ExecuteAsync(Ctx(true), args, default);
        Assert.True(result.IsError);
        Assert.Null(result.Events);
    }
}

public sealed class X402FlagTests
{
    [Fact]
    public void X402_capability_is_hidden_when_the_flag_is_off_and_registration_is_empty_by_default()
    {
        var registry = CapabilityRegistry.CreateDefault();
        var off = new GateContext(Biatec.Chat.Core.Data.SessionTier.Authenticated, ["openid", "sign"]);
        Assert.DoesNotContain(registry.ToolsFor(off), c => c.Name == "x402.pay");
        var on = new GateContext(Biatec.Chat.Core.Data.SessionTier.Authenticated, ["openid", "sign"], X402Enabled: true);
        Assert.Contains(registry.ToolsFor(on), c => c.Name == "x402.pay");

        var services = new ServiceCollection();
        X402Registration.AddX402Pay(services, new ConfigurationBuilder().Build());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IToolExecutor));
    }
}
