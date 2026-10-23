using Biatec.Chat.Api.Config;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Vault;

public static class VaultRegistration
{
    /// <summary>
    /// Registers the token vault. Everything is resolved lazily, so the app starts without Redis or AWS. An
    /// <see cref="ITokenRefresher"/> (the OIDC refresh grant, registered by AddOidcAuth) must be present.
    /// </summary>
    public static IServiceCollection AddTokenVault(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<VaultOptions>().Bind(config.GetSection(VaultOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<VaultOptions>, VaultOptionsValidator>();
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
            if (string.Equals(o.KeyProvider, "Secret", StringComparison.OrdinalIgnoreCase))
                return new SecretKeyProvider(Convert.FromBase64String(o.MasterKey!));
            var env = sp.GetRequiredService<IHostEnvironment>();
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                if (!o.AllowEphemeralKeys)
                    throw new InvalidOperationException("The Dev vault key provider is only allowed in Development. Set Vault:KeyProvider=Secret (or Kms), or set Vault:AllowEphemeralKeys=true to accept it.");
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Vault").LogWarning("The Dev vault key provider is active outside Development because Vault:AllowEphemeralKeys=true; set Vault:KeyProvider=Secret or Kms.");
            }
            return new DevKeyProvider(o.DevKey is null ? null : Convert.FromBase64String(o.DevKey));
        });
        services.TryAddSingleton<ITokenVault, TokenVault>();
        return services;
    }
}

/// <summary>Checks at startup that the chosen key provider has its key: Secret needs Vault:MasterKey, Kms needs Kms:KeyId.</summary>
public sealed class VaultOptionsValidator(IOptions<KmsOptions> kms) : IValidateOptions<VaultOptions>
{
    public ValidateOptionsResult Validate(string? name, VaultOptions o)
    {
        if (string.Equals(o.KeyProvider, "Dev", StringComparison.OrdinalIgnoreCase)) return ValidateOptionsResult.Success;
        if (string.Equals(o.KeyProvider, "Kms", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(kms.Value.KeyId)
                ? ValidateOptionsResult.Fail("Kms:KeyId is required when Vault:KeyProvider is Kms.")
                : ValidateOptionsResult.Success;
        if (string.Equals(o.KeyProvider, "Secret", StringComparison.OrdinalIgnoreCase))
            return IsBase64Of32Bytes(o.MasterKey)
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("Vault:MasterKey must be base64 of 32 bytes when Vault:KeyProvider is Secret.");
        return ValidateOptionsResult.Fail("Vault:KeyProvider must be 'Dev', 'Secret' or 'Kms'.");
    }

    private static bool IsBase64Of32Bytes(string? s)
    {
        var buffer = new byte[33];
        return !string.IsNullOrWhiteSpace(s) && Convert.TryFromBase64String(s, buffer, out var n) && n == 32;
    }
}
