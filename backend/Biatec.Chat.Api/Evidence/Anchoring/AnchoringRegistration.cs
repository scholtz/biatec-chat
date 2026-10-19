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
/// </summary>
public sealed class AlgodAnchorBroadcaster(IOptions<AnchoringOptions> options, IOptions<AlgodOptions> algod) : IAnchorBroadcaster
{
    public async Task<string> BroadcastAsync(byte[] note, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AccountMnemonic)) throw new InvalidOperationException("Evidence:Anchoring:AccountMnemonic is not configured.");
        if (!algod.Value.Networks.TryGetValue(o.Network, out var net)) throw new InvalidOperationException($"No Algod endpoint configured for '{o.Network}'.");

        using var http = new HttpClient { BaseAddress = new Uri(net.Url), Timeout = TimeSpan.FromSeconds(20) };
        if (net.Token is { Length: > 0 }) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Algo-API-Token", net.Token);
        var api = new DefaultApi(http);

        var account = new Algorand.Algod.Model.Account(o.AccountMnemonic);
        var p = await api.TransactionParamsAsync(ct);
        var tx = new PaymentTransaction
        {
            Sender = account.Address,
            Receiver = account.Address,
            Amount = 0,
            Fee = Math.Max(p.MinFee, 1000),
            FirstValid = p.LastRound,
            LastValid = p.LastRound + 1000,
            GenesisId = p.GenesisId,
            GenesisHash = new Digest(p.GenesisHash),
            Note = note,
        };
        var signed = tx.Sign(account);
        var txId = tx.TxID();
        await Algorand.Utils.Utils.SubmitTransaction(api, signed);
        var confirmed = await Algorand.Utils.Utils.WaitTransactionToComplete(api, txId, 20);
        if (confirmed is null) throw new TimeoutException($"Anchor transaction {txId} was not confirmed in time.");
        return txId;
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
        services.TryAddSingleton<AnchorGapTracker>();
        services.TryAddSingleton<IAnchorBroadcaster, AlgodAnchorBroadcaster>();
        services.TryAddScoped<AnchorService>();
        services.TryAddScoped<AnchoredEvidenceWriter>();
        if (config.GetSection(AnchoringOptions.Section).GetValue<bool>(nameof(AnchoringOptions.Enabled)))
            services.AddHostedService<AnchorHostedService>();
        return services;
    }
}
