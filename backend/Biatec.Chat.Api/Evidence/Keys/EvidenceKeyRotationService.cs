using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Core.Evidence;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Evidence.Keys;

/// <summary>Rotates the evidence signing key once it is older than <c>Evidence:RotationDays</c> (default 90; 0 disables). Retired keys stay in the JWKS.</summary>
public sealed class EvidenceKeyRotationService(IServiceProvider sp, IOptions<EvidenceOptions> options, ILogger<EvidenceKeyRotationService> log) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = options.Value.RotationDays;
        if (days <= 0) return;
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                if (sp.GetService<IEvidenceKeyStore>() is DbEvidenceKeyStore store
                    && await store.RotateIfOlderThanAsync(TimeSpan.FromDays(days), stoppingToken) is { } kid)
                    log.LogInformation("Evidence signing key rotated; new kid {Kid}", kid);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Evidence key rotation check failed; will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
