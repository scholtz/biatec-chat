using System.Net;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Tools.Signing;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Signing;

/// <summary>Expiry is only declared when the indexer confirms absence; algod's 404 alone proves nothing.</summary>
public sealed class AlgodTransactionConfirmerTests
{
    private const ulong LastValid = 1000;

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.ToString()));
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    private static Task<TxConfirmation> RunAsync(Func<string, HttpResponseMessage> respond, bool indexer = true)
    {
        var algod = new AlgodOptions();
        algod.Networks["testnet"] = new AlgodNetworkOptions { Url = "http://algod.test", IndexerUrl = indexer ? "http://indexer.test" : null };
        var c = new AlgodTransactionConfirmer(new Factory(new Handler(respond)), Options.Create(algod),
            Options.Create(new ConfirmationOptions { Timeout = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(1) }));
        return c.WaitAsync("algorand-testnet", "TX1", LastValid);
    }

    private static HttpResponseMessage Route(string url, string indexerTx, string indexerHealth = "{\"round\":2000}", HttpStatusCode indexerTxCode = HttpStatusCode.NotFound) => url switch
    {
        _ when url.StartsWith("http://algod.test/v2/transactions/pending/") => Json("{}", HttpStatusCode.NotFound),
        _ when url.StartsWith("http://algod.test/v2/status") => Json("{\"last-round\":2000}"),
        _ when url.StartsWith("http://indexer.test/v2/transactions/") => Json(indexerTx, indexerTxCode),
        _ when url.StartsWith("http://indexer.test/health") => Json(indexerHealth),
        _ => Json("{}", HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task PendingNotFound_PastLastValid_ButIndexerHasIt_IsConfirmed()
    {
        var r = await RunAsync(u => Route(u, "{\"transaction\":{\"confirmed-round\":777}}", indexerTxCode: HttpStatusCode.OK));
        Assert.Equal(TxConfirmationState.Confirmed, r.State);
        Assert.Equal(777UL, r.Round);
    }

    [Fact]
    public async Task PendingNotFound_PastLastValid_IndexerCaughtUpAndAbsent_IsExpired()
    {
        var r = await RunAsync(u => Route(u, "{}"));
        Assert.Equal(TxConfirmationState.Expired, r.State);
    }

    [Fact]
    public async Task PendingNotFound_PastLastValid_IndexerBehind_StaysPending()
    {
        var r = await RunAsync(u => Route(u, "{}", indexerHealth: "{\"round\":900}"));
        Assert.Equal(TxConfirmationState.Pending, r.State);
    }

    [Fact]
    public async Task PendingNotFound_PastLastValid_IndexerUnreachable_StaysPending()
    {
        var r = await RunAsync(u => Route(u, "{}", indexerTxCode: HttpStatusCode.BadGateway));
        Assert.Equal(TxConfirmationState.Pending, r.State);
    }

    [Fact]
    public async Task PendingNotFound_PastLastValid_NoIndexerConfigured_StaysPending()
    {
        var r = await RunAsync(u => Route(u, "{}"), indexer: false);
        Assert.Equal(TxConfirmationState.Pending, r.State);
    }
}
