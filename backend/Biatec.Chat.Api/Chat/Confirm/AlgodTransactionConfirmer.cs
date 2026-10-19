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

    public async Task<TxConfirmation> WaitAsync(string network, string txId, CancellationToken ct = default)
    {
        var o = options.Value;
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
            if (DateTimeOffset.UtcNow >= deadline) return new TxConfirmation(TxConfirmationState.Pending);
            await Task.Delay(o.PollInterval, ct);
        }
    }

    /// <summary>"algorand-testnet" and "testnet" both name the configured "testnet" endpoint.</summary>
    private bool TryResolve(string network, out AlgodNetworkOptions net)
    {
        var name = network.StartsWith("algorand-", StringComparison.OrdinalIgnoreCase) ? network["algorand-".Length..] : network;
        return algod.Value.Networks.TryGetValue(name, out net!) || algod.Value.Networks.TryGetValue(network, out net!);
    }
}
