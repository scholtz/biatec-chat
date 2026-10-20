using System.Net;
using System.Text;
using Biatec.Chat.Api.Safe.Proposals;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Safe.Proposals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Tests.Safe.Proposals;

/// <summary>The safe proposal card's algod reads go through the shared <see cref="IAlgodAssetReader"/> (fake algod, no live calls).</summary>
public sealed class SafeProposalHoldingsReaderTests
{
    private const string Addr = "UPYAFLHSIPMJOHVXU2MPLQ46GXJKSDCEMZ6RLCQ7GWB5PRDKJUWKKXECXI";

    private sealed class FakeAlgod : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<string, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.NotFound, "{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var (status, body) = Respond(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (AlgodSafeHoldingsReader Reader, FakeAlgod Algod) New()
    {
        var algod = new FakeAlgod();
        var factory = new AlgodClientFactory(new Dictionary<string, AlgodNetworkOptions> { ["algorand-testnet"] = new("https://testnet.example/") },
            _ => new HttpClient(algod));
        return (new AlgodSafeHoldingsReader(factory.Assets!), algod);
    }

    [Fact]
    public async Task Card_decimals_and_unit_names_come_from_the_shared_reader_on_the_mapped_network_and_are_cached()
    {
        var (reader, algod) = New();
        algod.Respond = p => p == "/v2/assets/10458941"
            ? (HttpStatusCode.OK, "{\"params\":{\"decimals\":6,\"unit-name\":\"US\\u0000DC\\u202e\"}}")
            : (HttpStatusCode.NotFound, "{}");

        var cleaned = AlgodAssetReader.CleanUnitName("US\u0000DC\u202e");
        Assert.DoesNotContain(cleaned!, c => char.IsControl(c) || c == '\u202e');
        Assert.Equal(new SafeAssetInfo(6, cleaned), await reader.AssetInfoAsync("testnet", 10458941));
        Assert.Equal(new SafeAssetInfo(6, cleaned), await reader.AssetInfoAsync("TestNet", 10458941));
        Assert.Equal("https://testnet.example/v2/assets/10458941", Assert.Single(algod.Requests));
        Assert.Null(await reader.AssetInfoAsync("mainnet", 10458941)); // not configured
    }

    [Fact]
    public async Task Holdings_read_through_the_shared_reader_and_only_algods_own_404_means_not_opted_in()
    {
        var (reader, algod) = New();
        algod.Respond = p => p switch
        {
            $"/v2/accounts/{Addr}/assets/1" => (HttpStatusCode.OK, "{\"asset-holding\":{\"amount\":42,\"asset-id\":1}}"),
            $"/v2/accounts/{Addr}/assets/2" => (HttpStatusCode.NotFound, "{\"message\":\"account asset info not found\"}"),
            _ => (HttpStatusCode.NotFound, "<html>proxy</html>"),
        };

        Assert.Equal(42UL, await reader.AssetAmountAsync("testnet", Addr, 1));
        Assert.Equal(0UL, await reader.AssetAmountAsync("testnet", Addr, 2));
        Assert.Null(await reader.AssetAmountAsync("testnet", Addr, 3));
        Assert.Null(await reader.AssetAmountAsync("mainnet", Addr, 1));
    }

    [Fact]
    public async Task Registration_uses_the_algod_factorys_asset_reader()
    {
        var algod = new FakeAlgod();
        var factory = new AlgodClientFactory(new Dictionary<string, AlgodNetworkOptions> { ["algorand-testnet"] = new("https://testnet.example/") },
            _ => new HttpClient(algod));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Builders:BaseAddress"] = "http://builders.test", ["Builders:InternalSecret"] = "test-only" }).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton(factory);
        services.AddSafeProposalTools(config);
        using var sp = services.BuildServiceProvider();

        var reader = Assert.IsType<AlgodSafeHoldingsReader>(sp.GetRequiredService<ISafeHoldingsReader>());
        algod.Respond = _ => (HttpStatusCode.OK, "{\"params\":{\"decimals\":2,\"unit-name\":\"EURe\"}}");
        Assert.Equal(new SafeAssetInfo(2, "EURe"), await reader.AssetInfoAsync("testnet", 7));
        Assert.Equal(new SafeAssetInfo(2, "EURe"), await factory.Assets!.GetAssetAsync("algorand-testnet", 7) is { } a ? new SafeAssetInfo(a.Decimals, a.UnitName) : null);
        Assert.Single(algod.Requests); // one cache shared with the transfer preview
    }
}
