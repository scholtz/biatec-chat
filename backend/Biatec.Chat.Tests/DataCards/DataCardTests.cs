using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Chat.DataCards;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Model.Guardrails;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Tests.DataCards;

/// <summary>The pure builder needs nothing; the orchestrator tests need the docker-compose postgres. Bedrock is a scripted fake.</summary>
public sealed class DataCardTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    // ALGO above 2^53 base units plus an ASA, as WalletBalanceExecutor shapes them.
    private const string Balance = """
        {"network":"testnet","address":"AAAA","balances":[
          {"assetId":"0","symbol":"ALGO","amount":"9007199254740993123","decimals":6,"display":"9007199254740.993123"},
          {"assetId":"31566704","symbol":"USDC","amount":"2500000","decimals":6,"display":"2.500000"}]}
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DataCard_Builder_KeepsAmountsAsDecimalStrings_AboveTwoToThe53()
    {
        var card = HoldingsCardBuilder.TryBuild(Balance, Now)!;
        Assert.Equal("holdings", card.Kind);
        Assert.Equal("testnet", card.Network);
        Assert.Equal(["AAAA"], card.Addresses);
        Assert.Equal(2, card.Assets.Count);
        Assert.Equal("9007199254740.993123", card.Assets[0].Amount);
        Assert.Equal("ALGO", card.Assets[0].UnitName);
        Assert.Equal(Now, card.AsOf);
        Assert.Null(card.FiatCurrency);
        Assert.All(card.Assets, a => Assert.Null(a.FiatValue));
        // The wire form carries the amount as a JSON string.
        var json = JsonSerializer.Serialize<ChatEvent>(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"type\":\"data_card\"", json);
        Assert.Contains("\"amount\":\"9007199254740.993123\"", json);
    }

    [Fact]
    public void DataCard_Builder_RealMcpAsaShape_DropsUnresolvableRows_AndMarksCardPartial()
    {
        const string json = """
            {"network":"testnet","address":"A","balances":[
              {"assetId":"0","symbol":"ALGO","amount":"1500000","decimals":6,"display":"1.500000"},
              {"assetId":"31566704","amount":"2500000"}]}
            """;
        var card = HoldingsCardBuilder.TryBuild(json, Now)!;
        Assert.True(card.Partial);
        Assert.Equal(["ALGO"], card.Assets.Select(a => a.UnitName));
        Assert.False(HoldingsCardBuilder.TryBuild(Balance, Now)!.Partial);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"network":"testnet","address":"A","balances":[]}""")]
    [InlineData("""{"network":"testnet","address":"A","assetsTruncated":true,"balances":[{"assetId":"0","symbol":"ALGO","amount":"1","decimals":6,"display":"0.000001"}]}""")]
    [InlineData("""{"network":"testnet","address":"A","balances":[{"assetId":"7","amount":"5"}]}""")]
    [InlineData("""{"network":"testnet","address":"A","balances":[{"raw":"something"}]}""")]
    public void DataCard_Builder_ProducesNoCard_WhenTheResultCannotBeShownExactly(string content) =>
        Assert.Null(HoldingsCardBuilder.TryBuild(content, Now));

    private sealed class ScriptedModel(params Func<IEnumerable<ModelStreamEvent>>[] scripts) : IModelClient
    {
        private int _n;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in scripts[Math.Min(_n++, scripts.Length - 1)]())
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class BalanceExecutor(string content, bool error = false) : IToolExecutor
    {
        public int Calls;
        public string CapabilityName => "wallet.balance";
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolExecutionResult(content, error));
        }
    }

    private sealed class AllowGuard : IToolResultGuard
    {
        public Task<GuardrailVerdict> ScreenAsync(string source, string text, CancellationToken ct) => Task.FromResult(GuardrailVerdict.Allowed);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
    private Tenant _tenant = null!;

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
        db.Tenants.Add(_tenant);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private async Task<List<ChatEvent>> RunAsync(SessionTier tier, BalanceExecutor exec)
    {
        await using var db = NewContext();
        var handle = await new SessionService(db, new FixedClock()).ResolveOrCreateAsync(_tenant, null, CancellationToken.None);
        var gate = new ToolGate(CapabilityRegistry.CreateDefault(), new InMemoryPolicyEventSink());
        var model = new ScriptedModel(
            () => ToolCall("t1", "wallet_balance", """{"network":"testnet","address":"AAAA"}"""),
            () => Text("You hold ALGO and USDC."));
        var orchestrator = new ConversationOrchestrator(model, gate, [exec], new DefaultMessageRedactor(), new AllowGuard(), db, new FixedClock(),
            NullLogger<ConversationOrchestrator>.Instance);
        var turn = new ChatTurn(handle.Session.ConversationId, handle.Session.Id.ToString("N"), _tenant, tier, tier == SessionTier.Authenticated ? ["openid"] : [], "my balance", null, true);
        var events = new List<ChatEvent>();
        await foreach (var e in orchestrator.RunAsync(turn)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task DataCard_WalletBalanceResult_ProducesExactlyOneHoldingsCard()
    {
        var exec = new BalanceExecutor(Balance);
        var events = await RunAsync(SessionTier.Authenticated, exec);

        Assert.Equal(1, exec.Calls);
        var card = Assert.Single(events.OfType<DataCardEvent>());
        Assert.Equal("holdings", card.Kind);
        Assert.Equal(["9007199254740.993123", "2.500000"], card.Assets.Select(a => a.Amount));
        // Card comes with the tool result, before the model's closing text.
        Assert.True(events.IndexOf(card) < events.FindIndex(e => e is TextDeltaEvent));
    }

    [Fact]
    public async Task DataCard_FailedWalletBalance_ProducesNoCard()
    {
        var events = await RunAsync(SessionTier.Authenticated, new BalanceExecutor("The balance could not be read.", error: true));
        Assert.Empty(events.OfType<DataCardEvent>());
    }

    [Fact]
    public async Task DataCard_AnonymousSession_NeverGetsAHoldingsCard_AndTheExecutorNeverRuns()
    {
        var exec = new BalanceExecutor(Balance);
        var events = await RunAsync(SessionTier.Anonymous, exec);
        Assert.Equal(0, exec.Calls);
        Assert.Empty(events.OfType<DataCardEvent>());
    }

    [Theory]
    [InlineData(SessionTier.Authenticated, "wallet.balance", true, false, true)]
    [InlineData(SessionTier.Anonymous, "wallet.balance", true, false, false)]
    [InlineData(SessionTier.Authenticated, "wallet.balance", false, false, false)]
    [InlineData(SessionTier.Authenticated, "wallet.balance", true, true, false)]
    [InlineData(SessionTier.Authenticated, "market.price", true, false, false)]
    public void ShouldBuild_RequiresSignedInExecutorSuccessOfWalletBalance(SessionTier tier, string cap, bool fromExecutor, bool isError, bool expected) =>
        Assert.Equal(expected, HoldingsCardBuilder.ShouldBuild(tier, cap, fromExecutor, isError));

    private static IEnumerable<ModelStreamEvent> Text(string text)
    {
        yield return new ModelTextDelta(text);
        yield return new ModelMessageStop("end_turn", new ModelMessage { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = text }] });
    }

    private static IEnumerable<ModelStreamEvent> ToolCall(string id, string name, string input)
    {
        yield return new ModelToolUse(id, name, input);
        yield return new ModelMessageStop("tool_use", new ModelMessage
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = id, Name = name, Input = DocumentJson.FromJson(input) } }],
        });
    }
}
