using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Market;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Market;
using Microsoft.Extensions.Options;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;

namespace Biatec.Chat.Tests.Market;

public sealed class MarketSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    // Newest bar is 11:00 UTC: the card must carry this, not the request time (12:00).
    private static readonly long LastBar = new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string History(decimal firstOpen, decimal lastClose) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $$"""
        {"s":"ok","t":[{{LastBar - 7200}},{{LastBar - 3600}},{{LastBar}}],
         "o":[{{firstOpen}},0.2100,0.2200],"h":[0.2300,0.2600,0.2500],"l":[0.2000,0.2100,0.1900],
         "c":[0.2100,0.2200,{{lastClose}}],"v":[1000000000000000000.5,2000.25,3000]}
        """);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static BiatecDexMarketClient Client(FakeHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://dex.test/") }, new FixedClock(Now));

    private static ToolExecutionContext Ctx() =>
        new(CapabilityRegistry.CreateDefault().Find(CapabilityRegistry.MarketSnapshot)!, "tu-1", Guid.NewGuid(), "s", "t", SessionTier.Anonymous, []);

    private static JsonObject Args(long a = 0, long b = 31566704) => new() { ["assetAId"] = a, ["assetBId"] = b };

    [Fact]
    public void Registry_MarketSnapshot_IsAnonymous_NoScopes_NoFunds()
    {
        var cap = CapabilityRegistry.CreateDefault().Find("market.snapshot")!;
        Assert.Equal(SessionTier.Anonymous, cap.MinTier);
        Assert.Empty(cap.BuildScopes);
        Assert.Null(cap.SignScope);
        Assert.False(cap.IsFundMoving);
    }

    [Fact]
    public void Tier_Anonymous_Tools_Are_KbSearch_RequestSignIn_And_Only_MarketSnapshot_Is_New()
    {
        var reg = CapabilityRegistry.CreateDefault();
        var anon = new GateContext(SessionTier.Anonymous, []);
        Assert.Equal(["kb.search", "market.snapshot", "requestSignIn"], reg.ToolsFor(anon).Select(c => c.ToolName!).OrderBy(x => x).ToArray());
        var gate = new ToolGate(reg, new InMemoryPolicyEventSink());
        Assert.True(gate.Check("market.snapshot", Args(), anon).Allowed);
        Assert.False(gate.Check("market.snapshot", new JsonObject { ["assetAId"] = 0 }, anon).Allowed);
    }

    [Fact]
    public async Task Backend_MockedDex_ReturnsDecimalStrings_AndTheTimestampOfTheData_NotOfTheRequest()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/asset-stat/")
            ? Json("""{"unitName":"USDC"}""")
            : Json(History(0.20m, 0.2150m)));
        var exec = new MarketSnapshotExecutor(Client(h));

        var r = await exec.ExecuteAsync(Ctx(), Args(), default);

        Assert.False(r.IsError);
        var card = Assert.IsType<DataCardEvent>(Assert.Single(r.Events!));
        Assert.Equal("market", card.Kind);
        Assert.Equal("ALGO/USDC", card.Pair);
        Assert.Equal("0.215", card.Price);
        Assert.Equal("7.5", card.Change24hPct); // (0.2150 - 0.20) / 0.20
        Assert.Equal("0.26", card.High24h);
        Assert.Equal("0.19", card.Low24h);
        Assert.Equal("1000000000000005000.75", card.Volume24h); // exact: no double rounding
        Assert.Equal(["0.21", "0.22", "0.215"], card.Series);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(LastBar), card.AsOf);
        Assert.NotEqual(Now, card.AsOf);
        Assert.Equal("Biatec DEX", card.Source);
        // Serialised, every number is a JSON string.
        var json = JsonSerializer.Serialize<ChatEvent>(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"price\":\"0.215\"", json);
        Assert.Contains("\"volume24h\":\"1000000000000005000.75\"", json);
        Assert.Contains("\"change24hPct\":\"7.5\"", json);
        // The model sees the same numbers and is told not to forecast.
        Assert.Contains("0.215", r.Content);
        Assert.Contains("No forecast", r.Content);
        Assert.Contains(h.Urls, u => u.Contains("assetA=0&assetB=31566704&resolution=60"));
    }

    [Fact]
    public async Task Backend_NegativeChange_IsSignedDecimal()
    {
        var h = new FakeHandler(_ => Json(History(0.25m, 0.2m)));
        var r = await new MarketSnapshotExecutor(Client(h)).ExecuteAsync(Ctx(), Args(0, 0 + 1), default);
        Assert.Equal("-20", Assert.IsType<DataCardEvent>(Assert.Single(r.Events!)).Change24hPct);
    }

    [Theory]
    [InlineData(500, "")]
    [InlineData(200, """{"s":"no_data"}""")]
    [InlineData(200, """{"s":"ok","t":[],"o":[],"h":[],"l":[],"c":[]}""")]
    [InlineData(200, "not json")]
    [InlineData(200, """{"s":"ok","t":[1],"o":["x"],"h":[1],"l":[1],"c":[1]}""")]
    [InlineData(200, """{"s":"ok","t":[1],"o":[0],"h":[0],"l":[0],"c":[0]}""")]
    public async Task UpstreamFailure_GivesPlainUnavailableAnswer_AndNoCard(int status, string body)
    {
        var h = new FakeHandler(_ => Json(body, (HttpStatusCode)status));
        var r = await new MarketSnapshotExecutor(Client(h)).ExecuteAsync(Ctx(), Args(), default);
        Assert.True(r.IsError);
        Assert.Null(r.Events);
        Assert.Contains("Market data unavailable", r.Content);
    }

    [Fact]
    public async Task UpstreamNetworkError_GivesUnavailable()
    {
        var h = new FakeHandler(_ => throw new HttpRequestException("down"));
        var r = await new MarketSnapshotExecutor(Client(h)).ExecuteAsync(Ctx(), Args(), default);
        Assert.True(r.IsError);
        Assert.Null(r.Events);
    }

    [Fact]
    public async Task SameAssetTwice_IsRefusedWithoutCallingUpstream()
    {
        var calls = 0;
        var fake = new CountingClient(() => calls++);
        var r = await new MarketSnapshotExecutor(fake).ExecuteAsync(Ctx(), Args(5, 5), default);
        Assert.True(r.IsError);
        Assert.Equal(0, calls);
    }

    private sealed class CountingClient(Action onCall) : IBiatecDexMarketClient
    {
        public Task<MarketSnapshot> GetSnapshotAsync(ulong assetA, ulong assetB, CancellationToken ct)
        {
            onCall();
            throw new MarketDataUnavailableException("x");
        }
    }

    [Fact]
    public async Task Cache_ReusesASnapshotBrieflyAndNeverCachesFailures()
    {
        var calls = 0;
        var fail = true;
        var clock = new FixedClock(Now);
        var inner = new DelegateClient(() =>
        {
            calls++;
            return fail ? throw new MarketDataUnavailableException("x")
                : new MarketSnapshot("ALGO/USDC", "1", "0", "1", "1", "1", ["1"], Now, "Biatec DEX");
        });
        var cache = new CachingMarketClient(inner, Options.Create(new MarketOptions { CacheDuration = TimeSpan.FromSeconds(60) }), clock);

        await Assert.ThrowsAsync<MarketDataUnavailableException>(() => cache.GetSnapshotAsync(0, 1, default));
        fail = false;
        await cache.GetSnapshotAsync(0, 1, default);
        await cache.GetSnapshotAsync(0, 1, default);
        Assert.Equal(2, calls);
        clock.Value = Now.AddSeconds(61);
        await cache.GetSnapshotAsync(0, 1, default);
        Assert.Equal(3, calls);
    }

    private sealed class DelegateClient(Func<MarketSnapshot> f) : IBiatecDexMarketClient
    {
        public Task<MarketSnapshot> GetSnapshotAsync(ulong assetA, ulong assetB, CancellationToken ct) => Task.FromResult(f());
    }

    [Fact]
    public async Task UnitNameFromTheChain_IsSanitised_AndFallsBackToTheAssetId()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/asset-stat/")
            ? Json("""{"unitName":"<script>x</script>"}""")
            : Json(History(0.2m, 0.2m)));
        var card = Assert.IsType<DataCardEvent>(Assert.Single((await new MarketSnapshotExecutor(Client(h)).ExecuteAsync(Ctx(), Args(0, 7), default)).Events!));
        Assert.Equal("ALGO/#7", card.Pair);
    }
}
