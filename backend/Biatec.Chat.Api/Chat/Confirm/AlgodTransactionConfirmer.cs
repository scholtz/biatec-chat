using System.Text.Json;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Tools.Signing;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Chat.Confirm;

public sealed class ConfirmationOptions
{
    public const string Section = "Confirmation";

    /// <summary>How long a Confirm request waits for the transaction to be confirmed before answering "submitted".</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Polls algod's pending-transaction endpoint (which also answers for recently confirmed transactions).</summary>
public sealed class AlgodTransactionConfirmer(IHttpClientFactory http, IOptions<AlgodOptions> algod, IOptions<ConfirmationOptions> options) : ITransactionConfirmer
{
    public const string HttpClientName = "algod-confirm";

    public async Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default)
    {
        var o = options.Value;
        var notFound = false;
        if (!TryResolve(network, out var net)) return new TxConfirmation(TxConfirmationState.Pending, Error: "network_not_configured");
        var client = http.CreateClient(HttpClientName);
        var deadline = DateTimeOffset.UtcNow + o.Timeout;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(net.Url.TrimEnd('/') + "/"), $"v2/transactions/pending/{Uri.EscapeDataString(txId)}"));
                if (!string.IsNullOrEmpty(net.Token)) req.Headers.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
                using var res = await client.SendAsync(req, ct);
                notFound = res.StatusCode == System.Net.HttpStatusCode.NotFound;
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("confirmed-round", out var cr) && cr.TryGetUInt64(out var round) && round > 0)
                        return new TxConfirmation(TxConfirmationState.Confirmed, round);
                    if (r.TryGetProperty("pool-error", out var pe) && pe.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pe.GetString()))
                        return new TxConfirmation(TxConfirmationState.Rejected, Error: "pool_error");
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                // Transient: keep polling until the deadline.
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                // 404 on the pending endpoint proves nothing: algod forgets confirmed transactions. Ask the indexer.
                if (notFound)
                {
                    var found = await IndexerLookupAsync(client, net, txId, ct);
                    if (found.Round is { } confirmedRound)
                        return new TxConfirmation(TxConfirmationState.Confirmed, confirmedRound);
                    // Expired only when the indexer is reachable, has no such transaction, has caught up past
                    // LastValid, and algod agrees the network is past it too.
                    if (found.Absent && lastValid > 0 && found.IndexerRound is { } ir && ir > lastValid
                        && await LastRoundAsync(client, net, ct) is { } round && round > lastValid)
                        return new TxConfirmation(TxConfirmationState.Expired, Error: "last_valid_passed");
                }
                return new TxConfirmation(TxConfirmationState.Pending);
            }
            await Task.Delay(o.PollInterval, ct);
        }
    }

    private readonly record struct IndexerResult(ulong? Round, bool Absent, ulong? IndexerRound);

    /// <summary>Looks the transaction up on the indexer. Unreachable or unconfigured is neither found nor absent.</summary>
    private static async Task<IndexerResult> IndexerLookupAsync(HttpClient client, AlgodNetworkOptions net, string txId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(net.IndexerUrl)) return default;
        try
        {
            var baseUri = new Uri(net.IndexerUrl.TrimEnd('/') + "/");
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"v2/transactions/{Uri.EscapeDataString(txId)}"));
            if (!string.IsNullOrEmpty(net.IndexerToken)) req.Headers.TryAddWithoutValidation("X-Indexer-API-Token", net.IndexerToken);
            using var res = await client.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("transaction", out var t) && t.TryGetProperty("confirmed-round", out var cr)
                    && cr.TryGetUInt64(out var round) && round > 0)
                    return new IndexerResult(round, false, null);
                return default;
            }
            if (res.StatusCode != System.Net.HttpStatusCode.NotFound) return default;

            using var hreq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
            if (!string.IsNullOrEmpty(net.IndexerToken)) hreq.Headers.TryAddWithoutValidation("X-Indexer-API-Token", net.IndexerToken);
            using var hres = await client.SendAsync(hreq, ct);
            if (!hres.IsSuccessStatusCode) return default;
            using var hdoc = JsonDocument.Parse(await hres.Content.ReadAsStringAsync(ct));
            var ir = hdoc.RootElement.TryGetProperty("round", out var r) && r.TryGetUInt64(out var rv) ? (ulong?)rv : null;
            return new IndexerResult(null, true, ir);
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException) && !ct.IsCancellationRequested)
        {
            return default;
        }
    }

    private static async Task<ulong?> LastRoundAsync(HttpClient client, AlgodNetworkOptions net, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(net.Url.TrimEnd('/') + "/"), "v2/status"));
            if (!string.IsNullOrEmpty(net.Token)) req.Headers.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("last-round", out var lr) && lr.TryGetUInt64(out var r) ? r : null;
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>"algorand-testnet" and "testnet" both name the configured "testnet" endpoint.</summary>
    private bool TryResolve(string network, out AlgodNetworkOptions net)
    {
        var name = network.StartsWith("algorand-", StringComparison.OrdinalIgnoreCase) ? network["algorand-".Length..] : network;
        return algod.Value.Networks.TryGetValue(name, out net!) || algod.Value.Networks.TryGetValue(network, out net!);
    }
}
