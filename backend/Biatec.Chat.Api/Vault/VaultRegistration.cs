using Biatec.Chat.Api.Config;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Vault;

public static class VaultRegistration
{
    /// <summary>
    /// Registers the token vault. Everything is resolved lazily, so the app starts without Redis or AWS. An
    /// <see cref="ITokenRefresher"/> (the OIDC refresh grant) must be registered by the auth code.
    /// </summary>
    public static IServiceCollection AddTokenVault(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<VaultOptions>(config.GetSection(VaultOptions.Section));
        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var cfg = ConfigurationOptions.Parse(sp.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString);
            cfg.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(cfg);
        });
        services.TryAddSingleton<IKeyProvider>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<VaultOptions>>().Value;
            if (string.Equals(o.KeyProvider, "Kms", StringComparison.OrdinalIgnoreCase))
                return new KmsKeyProvider(sp.GetRequiredService<IOptions<KmsOptions>>());
            if (!string.Equals(o.KeyProvider, "Dev", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Vault:KeyProvider must be 'Dev' or 'Kms'.");
            return new DevKeyProvider(o.DevKey is null ? null : Convert.FromBase64String(o.DevKey));
        });
        services.TryAddSingleton<ITokenRefresher, UnconfiguredTokenRefresher>();
        services.TryAddSingleton<ITokenVault, TokenVault>();
        return services;
    }
}

/// <summary>Placeholder until the OIDC refresh grant is registered; refreshing fails clearly rather than silently.</summary>
public sealed class UnconfiguredTokenRefresher : ITokenRefresher
{
    public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
        throw new NotSupportedException("No ITokenRefresher is registered; the OIDC refresh grant is provided by the auth flow.");
}
