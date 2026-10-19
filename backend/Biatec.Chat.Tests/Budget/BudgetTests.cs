using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Budget;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Tests.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Tenant = Biatec.Chat.Core.Data.Tenant;

namespace Biatec.Chat.Tests.Budget;

public class BudgetTests
{
    private const string Sonnet = "eu.anthropic.claude-sonnet-4-5-20250929-v1:0";

    private sealed class FakeModel(params ModelStreamEvent[] events) : IModelClient
    {
        public int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class MemoryLedger : IBudgetLedger
    {
        public long Tenant, Global;
        public bool Fail;
        public readonly List<(string TenantId, long MicroUsd)> Added = [];
        public Task<BudgetSpend> GetAsync(string tenantId, DateTimeOffset now, CancellationToken ct) =>
            Fail ? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down") : Task.FromResult(new BudgetSpend(Tenant, Global));
        public Task AddAsync(string tenantId, long microUsd, DateTimeOffset now, CancellationToken ct)
        {
            if (Fail) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
            Added.Add((tenantId, microUsd));
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCache : IAnswerCache
    {
        public readonly Dictionary<string, string> Items = [];
        public Task<string?> GetAsync(string tenantId, string question, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(tenantId + "|" + AnswerKey.Normalize(question)));
        public Task PutAsync(string tenantId, string question, string answer, CancellationToken ct)
        {
            Items[tenantId + "|" + AnswerKey.Normalize(question)] = answer;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTenant(Tenant? t) : IBudgetTenantSource
    {
        public Tenant? Current => t;
    }

    private static readonly ModelStreamEvent[] Reply =
    [
        new ModelTextDelta("Biatec is "),
        new ModelTextDelta("a DEX."),
        new ModelMessageStop("end_turn", null),
        new ModelUsage(Sonnet, 1000, 200, 5000, 0, 10),
    ];

    private static BudgetOptions Options() => new() { GlobalUsdPerWindow = 10m, TenantUsdPerWindow = 4m, ReservedAuthenticatedShare = 0.25m };

    private static BudgetedModelClient Client(IModelClient inner, MemoryLedger ledger, MemoryCache cache, BudgetOptions? o = null, Tenant? tenant = null,
        Biatec.Chat.Api.Telemetry.ChatMetrics? metrics = null) =>
        new(inner, ledger, cache, new FixedTenant(tenant ?? new Tenant { Id = "t1" }), Microsoft.Extensions.Options.Options.Create(o ?? Options()),
            TimeProvider.System, NullLogger<BudgetedModelClient>.Instance, metrics);

    /// <summary>Budget refusals seen on biatec.chat.refusals, by tier. Only this class emits reason budget_exhausted.</summary>
    private sealed class RefusalRecorder : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _l = new();
        public readonly List<string?> Tiers = [];
        public RefusalRecorder()
        {
            _l.InstrumentPublished = (i, l) => { if (i.Name == "biatec.chat.refusals") l.EnableMeasurementEvents(i); };
            _l.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var t = tags.ToArray().ToDictionary(x => x.Key, x => x.Value?.ToString());
                if (t.GetValueOrDefault("reason") == BudgetedModelClient.RefusalReason) lock (Tiers) Tiers.Add(t.GetValueOrDefault("tier"));
            });
            _l.Start();
        }
        public void Dispose() => _l.Dispose();
    }

    private static ModelRequest Request(ModelProfile profile, params string[] userTurns)
    {
        var messages = new List<Message>();
        foreach (var t in userTurns)
        {
            if (messages.Count > 0) messages.Add(new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "ok" }] });
            messages.Add(new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = t }, new ContentBlock { Text = "<host_page_context>route=/</host_page_context>" }] });
        }
        return new ModelRequest { Profile = profile, Persona = "p", Catalogue = "c", Messages = messages };
    }

    private static async Task<List<ModelStreamEvent>> Drain(IModelClient c, ModelRequest r)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in c.StreamAsync(r)) list.Add(e);
        return list;
    }

    private static string Text(IEnumerable<ModelStreamEvent> events) => string.Concat(events.OfType<ModelTextDelta>().Select(d => d.Text));

    [Fact]
    public void Pricing_CountsEachTokenKindAndRoundsUp()
    {
        var p = ModelPrices.For(Sonnet, []);
        // 1000 × 3.30 + 200 × 16.50 + 5000 × 0.33 + 100 × 4.125 = 8662.5 micro-USD
        Assert.Equal(8663, ModelPrices.MicroUsd(new ModelUsage(Sonnet, 1000, 200, 5000, 100, 0), p));
        Assert.Equal(1, ModelPrices.MicroUsd(new ModelUsage(Sonnet, 0, 0, 1, 0, 0), p));
    }

    [Fact]
    public void Pricing_UnknownModelChargedAtMostExpensive_ConfiguredWins()
    {
        Assert.Equal(27.50m, ModelPrices.For("eu.anthropic.claude-new", []).OutputUsdPerMTok);
        var configured = new List<ModelPrice> { new() { ModelId = Sonnet, OutputUsdPerMTok = 1m } };
        Assert.Equal(1m, ModelPrices.For(Sonnet, configured).OutputUsdPerMTok);
    }

    [Fact]
    public async Task Usage_IsAddedToTheTenantsWindow()
    {
        var ledger = new MemoryLedger();
        var events = await Drain(Client(new FakeModel(Reply), ledger, new MemoryCache()), Request(ModelProfile.Authenticated, "hi"));
        Assert.Equal(Reply, events);
        // 1000 × 3.30 + 200 × 16.50 + 5000 × 0.33 = 8250
        Assert.Equal([("t1", 8250L)], ledger.Added);
    }

    [Fact]
    public async Task Anonymous_PastItsShare_GetsRefusalWithSignInHint_WithoutCallingBedrock()
    {
        var model = new FakeModel(Reply);
        // Tenant cap 4 USD, anonymous share 3 USD.
        var ledger = new MemoryLedger { Tenant = 3_000_000 };
        var events = await Drain(Client(model, ledger, new MemoryCache()), Request(ModelProfile.Anonymous, "What is Biatec?"));
        Assert.Equal(0, model.Calls);
        Assert.Equal(BudgetedModelClient.AnonymousRefusal + BudgetedModelClient.SignInHint, Text(events));
        Assert.IsType<ModelMessageStop>(events[^1]);
    }

    [Fact]
    public async Task Anonymous_PastItsShare_GetsCachedAnswerForTheSameQuestion()
    {
        var cache = new MemoryCache();
        var ledger = new MemoryLedger();
        await Drain(Client(new FakeModel(Reply), ledger, cache), Request(ModelProfile.Anonymous, "What is Biatec?"));

        ledger.Global = 7_500_000; // global cap 10 USD, anonymous share 7.5 USD
        var model = new FakeModel(Reply);
        var events = await Drain(Client(model, ledger, cache), Request(ModelProfile.Anonymous, "  what is biatec  "));
        Assert.Equal(0, model.Calls);
        Assert.Equal("Biatec is a DEX.", Text(events));
    }

    [Fact]
    public async Task Authenticated_KeepsTheReservedShare()
    {
        var model = new FakeModel(Reply);
        var ledger = new MemoryLedger { Tenant = 3_500_000 };
        await Drain(Client(model, ledger, new MemoryCache()), Request(ModelProfile.Authenticated, "hi"));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task FullyUsed_RefusesAuthenticated_AndAnonymousWithoutHint()
    {
        var model = new FakeModel(Reply);
        var ledger = new MemoryLedger { Global = 10_000_000 };
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => Drain(Client(model, ledger, new MemoryCache()), Request(ModelProfile.Authenticated, "hi")));
        var anon = await Drain(Client(model, ledger, new MemoryCache()), Request(ModelProfile.Anonymous, "hi"));
        Assert.Equal(BudgetedModelClient.AnonymousRefusal, Text(anon));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task OnlySingleQuestionToolFreeAnswersAreCached()
    {
        var cache = new MemoryCache();
        var ledger = new MemoryLedger();
        await Drain(Client(new FakeModel(Reply), ledger, cache), Request(ModelProfile.Anonymous, "first", "second"));
        await Drain(Client(new FakeModel(new ModelToolUse("1", "kb_search", "{}"), new ModelMessageStop("tool_use", null)), ledger, cache), Request(ModelProfile.Anonymous, "q"));
        await Drain(Client(new FakeModel(Reply), ledger, cache), Request(ModelProfile.Authenticated, "auth"));
        Assert.Empty(cache.Items);
        await Drain(Client(new FakeModel(Reply), ledger, cache), Request(ModelProfile.Anonymous, "q"));
        Assert.Equal("Biatec is a DEX.", Assert.Single(cache.Items).Value);
    }

    [Fact]
    public async Task LedgerOutage_FailsOpen()
    {
        var model = new FakeModel(Reply);
        var events = await Drain(Client(model, new MemoryLedger { Fail = true }, new MemoryCache()), Request(ModelProfile.Anonymous, "hi"));
        Assert.Equal(1, model.Calls);
        Assert.Equal(Reply, events);
    }

    [Fact]
    public async Task Disabled_PassesThrough()
    {
        var ledger = new MemoryLedger { Global = long.MaxValue };
        var o = Options();
        o.Enabled = false;
        var model = new FakeModel(Reply);
        await Drain(Client(model, ledger, new MemoryCache(), o), Request(ModelProfile.Authenticated, "hi"));
        Assert.Equal(1, model.Calls);
        Assert.Empty(ledger.Added);
    }

    [Theory]
    [InlineData("{\"usdPerWindow\": 2.5}", 2.5)]
    [InlineData("{}", 4)]
    [InlineData("{\"usdPerWindow\": -1}", 4)]
    [InlineData("not json", 4)]
    [InlineData("[]", 4)]
    public void TenantBudget_FromBedrockBudgets(string json, double expected)
    {
        var c = Client(new FakeModel(), new MemoryLedger(), new MemoryCache());
        Assert.Equal((decimal)expected, c.TenantUsd(new Tenant { BedrockBudgets = json }));
    }

    [Fact]
    public async Task TenantOverride_IsEnforced()
    {
        var model = new FakeModel(Reply);
        var ledger = new MemoryLedger { Tenant = 1_000_000 };
        var tenant = new Tenant { Id = "small", BedrockBudgets = "{\"usdPerWindow\": 1}" };
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => Drain(Client(model, ledger, new MemoryCache(), tenant: tenant), Request(ModelProfile.Authenticated, "hi")));
    }

    [Fact]
    public void Registration_DecoratesTheRegisteredClient()
    {
        var inner = new FakeModel(Reply);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IModelClient>(inner);
        services.AddSingleton<IBudgetLedger>(new MemoryLedger());
        services.AddSingleton<IAnswerCache>(new MemoryCache());
        services.AddBedrockBudget();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<BudgetedModelClient>(sp.GetRequiredService<IModelClient>());
        Assert.Single(services, d => d.ServiceType == typeof(IModelClient));
    }

    [RedisFact]
    public async Task RedisLedger_CountsPerTenantAndGlobally_PerWindow()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var ledger = new RedisBudgetLedger(mux, Microsoft.Extensions.Options.Options.Create(new BudgetOptions { WindowMinutes = 60 }));
        var a = "t-" + Guid.NewGuid().ToString("N");
        var b = "t-" + Guid.NewGuid().ToString("N");
        // A window far in the future, so other tests' global spend doesn't show up.
        var now = new DateTimeOffset(2099, 1, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(1, 500_000) * 60);
        var before = await ledger.GetAsync(a, now, default);
        await ledger.AddAsync(a, 100, now, default);
        await ledger.AddAsync(b, 50, now.AddMinutes(30), default);
        var spend = await ledger.GetAsync(a, now, default);
        Assert.Equal(100, spend.TenantMicroUsd);
        Assert.Equal(before.GlobalMicroUsd + 150, spend.GlobalMicroUsd);
        Assert.Equal(0, (await ledger.GetAsync(a, now.AddMinutes(60), default)).TenantMicroUsd);
    }

    [RedisFact]
    public async Task RedisCache_HashesTheQuestionAndExpires()
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.Conn);
        var cache = new RedisAnswerCache(mux, Microsoft.Extensions.Options.Options.Create(new BudgetOptions { CachedAnswerTtlMinutes = 5 }));
        var tenant = "t-" + Guid.NewGuid().ToString("N");
        var question = "Secret question " + Guid.NewGuid();
        var answer = "answer " + Guid.NewGuid();
        await cache.PutAsync(tenant, question, answer, default);
        Assert.Equal(answer, await cache.GetAsync(tenant, question.ToUpperInvariant() + "?", default));
        Assert.Null(await cache.GetAsync("other", question, default));

        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var keys = server.Keys(pattern: "biatec:budget:answer:*").Select(k => k.ToString()).ToList();
        Assert.DoesNotContain(keys, k => k.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        var mine = keys.Where(k => mux.GetDatabase().StringGet(k) == answer).ToList();
        Assert.Single(mine);
        Assert.All(mine, k => Assert.InRange(mux.GetDatabase().KeyTimeToLive(k)!.Value.TotalMinutes, 4, 5));
    }

    [Fact]
    public async Task Refusals_AreCountedPerTier_CachedAnswersAreNot()
    {
        using var rec = new RefusalRecorder();
        using var metrics = new Biatec.Chat.Api.Telemetry.ChatMetrics();
        var cache = new MemoryCache();
        await cache.PutAsync("t1", "cached question", "cached answer", default);

        var anonymousUsed = new MemoryLedger { Tenant = 3_000_000 };
        await Drain(Client(new FakeModel(Reply), anonymousUsed, cache, metrics: metrics), Request(ModelProfile.Anonymous, "new question"));
        await Drain(Client(new FakeModel(Reply), anonymousUsed, cache, metrics: metrics), Request(ModelProfile.Anonymous, "cached question"));
        await Drain(Client(new FakeModel(Reply), anonymousUsed, cache, metrics: metrics), Request(ModelProfile.Authenticated, "within reserve"));

        var fullyUsed = new MemoryLedger { Global = 10_000_000 };
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => Drain(Client(new FakeModel(Reply), fullyUsed, cache, metrics: metrics), Request(ModelProfile.Authenticated, "hi")));

        Assert.Equal(["anonymous", "authenticated"], rec.Tiers);
    }

    [Fact]
    public async Task Refusals_WithoutTelemetry_StillRefuse()
    {
        // ChatMetrics is only registered when telemetry is on; the client works the same without it.
        var registered = new ServiceCollection();
        registered.AddLogging();
        registered.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        registered.AddSingleton<IModelClient>(new FakeModel(Reply));
        registered.AddSingleton<IBudgetLedger>(new MemoryLedger { Global = 20_000_000 }); // the default global budget is 20 USD
        registered.AddSingleton<IAnswerCache>(new MemoryCache());
        registered.AddBedrockBudget();
        using var sp = registered.BuildServiceProvider();
        var events = await Drain(sp.GetRequiredService<IModelClient>(), Request(ModelProfile.Anonymous, "hi"));
        Assert.Equal(BudgetedModelClient.AnonymousRefusal, Text(events));
    }
}
