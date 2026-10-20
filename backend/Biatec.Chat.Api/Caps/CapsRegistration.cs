using Biatec.Chat.Api.Sweeper;
using Biatec.Chat.Tools.Caps;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Caps;

public static class CapsRegistration
{
    /// <summary>Tenant soft cap and safe caps (T-025): the confirm path reserves through <see cref="ISpendCaps"/>; rows are pruned every sweeper interval.</summary>
    public static IServiceCollection AddSpendCaps(this IServiceCollection services)
    {
        services.TryAddScoped<ISpendCaps, SpendCaps>();
        services.AddHostedService<CapsPruneHostedService>();
        return services;
    }
}

/// <summary>
/// Releases reservations of expired previews and prunes <c>SoftCapSpend</c> and <c>SafeApprovalCount</c> rows older
/// than the longest window plus one hour. Runs on the sweeper's schedule and switch; a failure is logged by type.
/// </summary>
public sealed class CapsPruneHostedService(IServiceScopeFactory scopes, IOptions<SweeperOptions> options, TimeProvider clock, ILogger<CapsPruneHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ISpendCaps>().PruneAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError("Cap pruning failed: {ErrorType}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
