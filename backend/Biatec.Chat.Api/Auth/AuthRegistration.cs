using Biatec.Chat.Api.Vault;

namespace Biatec.Chat.Api.Auth;

public static class AuthRegistration
{
    /// <summary>OIDC client, metadata, ID token validation, the pending-state store and the vault's refresh grant. Call before AddTokenVault.</summary>
    public static IServiceCollection AddOidcAuth(this IServiceCollection services)
    {
        services.AddHttpClient(OidcMetadataProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IOidcMetadataProvider, OidcMetadataProvider>();
        services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();
        services.AddSingleton<IOidcClient, OidcClient>();
        services.AddSingleton<IIdTokenValidator, IdTokenValidator>();
        services.AddSingleton<IAuthStateStore, RedisAuthStateStore>();
        services.AddSingleton<ITokenRefresher, OidcTokenRefresher>();
        return services;
    }
}
