namespace Biatec.Chat.Api.Onboarding;

/// <summary>Resolves DNS TXT records. Behind an interface so tests use a fake and never touch the network.</summary>
public interface IDnsTxtResolver
{
    /// <summary>All TXT strings at <paramref name="name"/>; empty when the name has none or does not exist.</summary>
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct);
}

/// <summary>A confidential client registered at Biatec OIDC.</summary>
public sealed record RegisteredClient(string ClientId, string ClientSecret);

/// <summary>
/// RFC 7591 dynamic registration of a confidential client at Biatec OIDC. Returns null when registration is not
/// available (today Biatec OIDC issues public clients only), which sends onboarding down the operator-approval fallback.
/// </summary>
public interface IOidcClientRegistrar
{
    Task<RegisteredClient?> RegisterAsync(string clientName, string redirectUri, CancellationToken ct);
}

/// <summary>The deployment secret store (AWS Secrets Manager or Kubernetes secrets). The database only ever gets the reference.</summary>
public interface ISecretStore
{
    /// <summary>Stores <paramref name="value"/> under <paramref name="path"/> and returns the reference (<c>secret://path</c>).</summary>
    Task<string> PutAsync(string path, string value, CancellationToken ct);

    /// <summary>Best-effort removal of a secret written by <see cref="PutAsync"/> (cleanup after a failed onboarding).</summary>
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Default registrar: Biatec OIDC dynamic confidential-client registration has not shipped (docs/required-changes-biatec-oidc.md).</summary>
public sealed class NoDynamicRegistration : IOidcClientRegistrar
{
    public Task<RegisteredClient?> RegisterAsync(string clientName, string redirectUri, CancellationToken ct) =>
        Task.FromResult<RegisteredClient?>(null);
}

/// <summary>Default secret store until a deployment binds one; never reached while registration is unavailable.</summary>
public sealed class UnconfiguredSecretStore : ISecretStore
{
    public Task<string> PutAsync(string path, string value, CancellationToken ct) =>
        throw new InvalidOperationException("No secret store is configured for tenant onboarding.");

    public Task DeleteAsync(string path, CancellationToken ct) => Task.CompletedTask;
}
