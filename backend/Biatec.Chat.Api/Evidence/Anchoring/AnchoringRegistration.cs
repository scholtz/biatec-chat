using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Api.Config;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Evidence.Anchoring;

/// <summary>
/// Sends the anchor as a 0 ALGO self-payment from the evidence account with the note, signed locally by that account's
/// key. The evidence account is a dedicated, publicly known account; its mnemonic comes from configuration supplied
/// through the environment or a secret store, never from a committed file.
/// <para>
/// Idempotent per note: the signed transaction is kept until it confirmed or its LastValid round passed. A retry (after
/// a timeout or an HTTP error that may have happened after the node accepted the transaction) first asks the node
/// whether it confirmed, re-submits the same signed bytes while it can still be confirmed, and sends a new transaction
/// only once the first can no longer land. So one note never ends up in two confirmed transactions.
/// </para>
/// </summary>
public sealed class AlgodAnchorBroadcaster(IOptions<AnchoringOptions> options, IOptions<AlgodOptions> algod, HttpMessageHandler? handler = null) : IAnchorBroadcaster
{
    private sealed record Submitted(string TxId, SignedTransaction Signed, ulong LastValid);

    private readonly Dictionary<string, Submitted> _submitted = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> BroadcastAsync(byte[] note, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AccountMnemonic)) throw new InvalidOperationException("Evidence:Anchoring:AccountMnemonic is not configured.");
        if (!algod.Value.Networks.TryGetValue(o.Network, out var net)) throw new InvalidOperationException($"No Algod endpoint configured for '{o.Network}'.");

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(net.Url);
        http.Timeout = TimeSpan.FromSeconds(20);
        if (net.Token is { Length: > 0 }) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
        var api = new DefaultApi(http);

        var key = Convert.ToBase64String(note);
        await _gate.WaitAsync(ct);
        try
        {
            var p = await api.TransactionParamsAsync(ct);
            if (_submitted.TryGetValue(key, out var earlier))
            {
                if (await IsConfirmedAsync(http, earlier.TxId, ct)) { _submitted.Remove(key); return earlier.TxId; }
                if (p.LastRound <= earlier.LastValid)
                {
                    // Still able to confirm: re-submit the very same transaction (same txId) and wait again.
                    try { await Algorand.Utils.Utils.SubmitTransaction(api, earlier.Signed); }
                    catch (Exception e) when (e is not OperationCanceledException) { /* already in the pool or the ledger: the poll below decides */ }
                    return await WaitAsync(http, key, earlier, o, ct);
                }
                // LastValid has passed and it never confirmed: it can no longer land, so a new transaction is safe.
                _submitted.Remove(key);
            }

            var account = new Algorand.Algod.Model.Account(o.AccountMnemonic);
            var tx = new PaymentTransaction
            {
                Sender = account.Address,
                Receiver = account.Address,
                Amount = 0,
                Fee = Math.Max(p.MinFee, 1000),
                FirstValid = p.LastRound,
                LastValid = p.LastRound + (ulong)Math.Max(1, o.ValidRounds),
                GenesisId = p.GenesisId,
                GenesisHash = new Digest(p.GenesisHash),
                Note = note,
            };
            var created = new Submitted(tx.TxID(), tx.Sign(account), tx.LastValid);
            _submitted[key] = created; // remembered before the submit: an error after the node accepted it must not lose the txId
            await Algorand.Utils.Utils.SubmitTransaction(api, created.Signed);
            return await WaitAsync(http, key, created, o, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<AnchorResolution> ResolveAsync(byte[] note, CancellationToken ct)
    {
        var o = options.Value;
        if (!algod.Value.Networks.TryGetValue(o.Network, out var net)) throw new InvalidOperationException($"No Algod endpoint configured for '{o.Network}'.");
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(net.Url);
        http.Timeout = TimeSpan.FromSeconds(20);
        if (net.Token is { Length: > 0 }) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
        var api = new DefaultApi(http);
        var key = Convert.ToBase64String(note);
        await _gate.WaitAsync(ct);
        try
        {
            if (!_submitted.TryGetValue(key, out var s)) return new AnchorResolution(AnchorResolutionKind.Unknown);
            if (await IsConfirmedAsync(http, s.TxId, ct)) { _submitted.Remove(key); return new AnchorResolution(AnchorResolutionKind.Confirmed, s.TxId); }
            var p = await api.TransactionParamsAsync(ct);
            if (p.LastRound <= s.LastValid) return new AnchorResolution(AnchorResolutionKind.Pending);
            _submitted.Remove(key); // can no longer land: lost is definitive and the entry is dropped
            return new AnchorResolution(AnchorResolutionKind.Expired);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> WaitAsync(HttpClient http, string key, Submitted s, AnchoringOptions o, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + o.ConfirmTimeout;
        while (true)
        {
            if (await IsConfirmedAsync(http, s.TxId, ct)) { _submitted.Remove(key); return s.TxId; }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Anchor transaction {s.TxId} was not confirmed in time; it is checked again on retry.");
            await Task.Delay(o.ConfirmPollInterval, ct);
        }
    }

    /// <summary>True when the node reports a confirmed round for the transaction (404 means unknown or not yet in a block).</summary>
    private static async Task<bool> IsConfirmedAsync(HttpClient http, string txId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"v2/transactions/pending/{txId}", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        res.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("confirmed-round", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number && r.GetUInt64() > 0;
    }
}

/// <summary>Runs <see cref="AnchorService.TickAsync"/> every <see cref="AnchoringOptions.Interval"/> while anchoring is enabled.</summary>
public sealed class AnchorHostedService(IServiceScopeFactory scopes, IOptions<AnchoringOptions> options, ILogger<AnchorHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<AnchorService>().TickAsync(stoppingToken);
                if (result.Anchor is not null) log.LogInformation("Anchored {Leaves} conversation(s) in anchor {Seq}, tx {Tx}.", result.Leaves, result.Anchor.SequenceNumber, result.Anchor.AnchorTxId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Anchor tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

public static class AnchoringRegistration
{
    /// <summary>Registers the anchor service and writer; the periodic job runs only when <c>Evidence:Anchoring:Enabled</c> is true.</summary>
    public static IServiceCollection AddEvidenceAnchoring(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnchoringOptions>(config.GetSection(AnchoringOptions.Section));
        services.TryAddSingleton<IAnchorBroadcaster, AlgodAnchorBroadcaster>();
        services.TryAddScoped<AnchorService>();
        services.TryAddScoped<AnchoredEvidenceWriter>();
        if (config.GetSection(AnchoringOptions.Section).GetValue<bool>(nameof(AnchoringOptions.Enabled)))
            services.AddHostedService<AnchorHostedService>();
        return services;
    }
}
