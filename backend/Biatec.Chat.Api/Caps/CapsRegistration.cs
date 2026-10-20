using Biatec.Chat.Api.Sweeper;
using Biatec.Chat.Tools.Caps;
using Biatec.Chat.Tools.Caps.NoDevice;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Caps;

public static class CapsRegistration
{
    /// <summary>Tenant soft cap and safe caps (T-025): the confirm path reserves through <see cref="ISpendCaps"/>; rows are pruned every sweeper interval.</summary>
    public static IServiceCollection AddSpendCaps(this IServiceCollection services)
    {
        // Caps:OidcHonoursRequireConsent stays false against real Biatec OIDC (required-changes row 3); true only with the mock.
        services.AddOptions<CapsOptions>().BindConfiguration(CapsOptions.Section);
        // T-061: reduced daily cap for users without an active device; Caps:NoDevice:Enabled = false once Biatec OIDC enforces rule 4 (required-changes row 10).
        services.AddOptions<NoDeviceCapOptions>().BindConfiguration(NoDeviceCapOptions.Section);
        services.TryAddScoped<ISpendCaps>(sp => new SpendCaps(sp.GetRequiredService<Biatec.Chat.Core.Data.ChatDbContext>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<CapsOptions>>().Value,
            sp.GetRequiredService<IOptions<NoDeviceCapOptions>>().Value));
        services.TryAddSingleton(sp => new NoDeviceSettingsCache(sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<INoDeviceGate>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NoDeviceCapOptions>>().Value;
            if (!options.Enabled) return DisabledNoDeviceGate.Instance; // resolves nothing else
            return new NoDeviceGate(sp.GetRequiredService<Biatec.Chat.Tools.Consent.IConsentSettingsClient>(),
                () => SettingsEndpoint(sp.GetRequiredService<IOptions<Biatec.Chat.Api.Config.OidcOptions>>()),
                options, sp.GetRequiredService<NoDeviceSettingsCache>());
        });
        services.AddHostedService<CapsPruneHostedService>();
        return services;
    }

    /// <summary>Biatec OIDC <c>GET /consent/settings</c>; null (the backend cannot tell) when the issuer is unset or invalid.</summary>
    private static Uri? SettingsEndpoint(IOptions<Biatec.Chat.Api.Config.OidcOptions> oidc)
    {
        try
        {
            return Uri.TryCreate(oidc.Value.Issuer?.TrimEnd('/') + "/consent/settings", UriKind.Absolute, out var u)
                   && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u : null;
        }
        catch (OptionsValidationException) { return null; }
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
