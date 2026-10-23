using System.Text.Json.Nodes;
using Biatec.Chat.Api.Evidence.Keys;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Evidence.Jwks;

public sealed class EvidenceOptions
{
    public const string Section = "Evidence";

    /// <summary>Optional file for the local dev signing keys (so they survive restarts). Never a production setting.</summary>
    public string? DevKeyFile { get; set; }

    /// <summary>Key store: <c>Secret</c> (HKDF-derived from a master secret, metadata in PostgreSQL), <c>Kms</c> (pre-created KMS keys) or <c>Dev</c> (in-memory/file, Development only). Required outside Development.</summary>
    public string? KeyStore { get; set; }

    /// <summary>Base64 master secret (at least 32 bytes) for KeyStore=Secret; comes from a Kubernetes secret, never from appsettings.</summary>
    public SecretOptions Secret { get; set; } = new();

    public KmsStoreOptions Kms { get; set; } = new();

    /// <summary>Automatic rotation: when the active key is older than this many days it is retired and a new one activated (checked hourly by every replica). 0 disables it; the default is 90.</summary>
    public int RotationDays { get; set; } = 90;

    public sealed class SecretOptions { public string? MasterKey { get; set; } }

    public sealed class KmsStoreOptions
    {
        public string Region { get; set; } = "eu-central-1";
        /// <summary>Pre-created ECC_NIST_P256 SIGN_VERIFY keys, oldest first; rotation activates the next unused one.</summary>
        public List<string> KeyIds { get; set; } = [];
    }

    /// <summary>Outside Development the in-memory dev key store is refused unless this is true (keys are lost on restart).</summary>
    public bool AllowEphemeralKeys { get; set; }
}

public static class EvidenceJwks
{
    public const string JwksPath = "/.well-known/evidence-jwks.json";

    /// <summary>
    /// Where a bundle's verifier fetches the keys. The configured public base URL wins (behind a proxy the request scheme and
    /// host can be internal); otherwise the request's own origin plus its PathBase, so a host mounted under a path prefix
    /// (Hosting:Mounts) gets a URL that still reaches this backend.
    /// </summary>
    public static string PublicUrl(HttpContext ctx, IConfiguration config)
    {
        var publicBase = config["Evidence:PublicBaseUrl"]?.Trim().TrimEnd('/');
        return (string.IsNullOrEmpty(publicBase) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}" : publicBase) + JwksPath;
    }

    /// <summary>Registers the evidence log and the dev signing key store (register a production <see cref="IEvidenceKeyStore"/> first to replace it).</summary>
    public static IServiceCollection AddEvidence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EvidenceOptions>(config.GetSection(EvidenceOptions.Section));
        services.TryAddSingleton<IEvidenceKeyStore>(sp =>
        {
            var o = config.GetSection(EvidenceOptions.Section).Get<EvidenceOptions>() ?? new EvidenceOptions();
            var dev = sp.GetRequiredService<IHostEnvironment>().IsDevelopment();
            switch ((o.KeyStore ?? "").Trim().ToLowerInvariant())
            {
                case "secret":
                    // Every signing key is derived from this one master, so a rotation gives no recovery from its compromise (BCHAT-014).
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Evidence").LogWarning(
                        "Evidence:KeyStore=Secret derives every signing key from one master secret: rotating keys does not recover from its compromise, and past and future signatures could be forged. Prefer Evidence:KeyStore=Kms for production (docs/threat-model.md).");
                    return new DbEvidenceKeyStore(sp.GetRequiredService<IServiceScopeFactory>(), SecretEvidenceKeyMaterial.FromBase64(o.Secret.MasterKey));
                case "kms":
                    if (!string.Equals(o.Kms.Region, "eu-central-1", StringComparison.Ordinal))
                        throw new InvalidOperationException("Evidence:Kms:Region must be eu-central-1.");
                    return new DbEvidenceKeyStore(sp.GetRequiredService<IServiceScopeFactory>(), KmsEvidenceKeyMaterial.Create(o.Kms.Region, o.Kms.KeyIds));
                case "" or "dev":
                    break;
                default:
                    throw new InvalidOperationException("Evidence:KeyStore must be 'Secret', 'Kms' or 'Dev'.");
            }
            if (!dev && !string.IsNullOrWhiteSpace(o.DevKeyFile))
                throw new InvalidOperationException("Evidence:DevKeyFile is only allowed in Development; register a production IEvidenceKeyStore.");
            if (!dev && !o.AllowEphemeralKeys)
                throw new InvalidOperationException("No evidence key store is configured. Set Evidence:KeyStore=Secret (or Kms), or set Evidence:AllowEphemeralKeys=true to accept signing keys that are lost on restart.");
            if (!dev)
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Evidence").LogWarning("Evidence signing uses the local Dev key store; register a production IEvidenceKeyStore.");
            return new DevEvidenceKeyStore(o.DevKeyFile);
        });
        services.TryAddScoped<IEvidenceLog>(sp => new EvidenceLog(sp.GetRequiredService<ChatDbContext>(), sp.GetRequiredService<IEvidenceKeyStore>()));
        services.AddHostedService<EvidenceKeyRotationService>();
        return services;
    }

    /// <summary>Public JWKS: every key ever used for signing, retired ones marked with <c>retired_at</c> (RFC 3339).</summary>
    public static JsonObject ToJwks(IEnumerable<EvidencePublicKey> keys) => new()
    {
        ["keys"] = new JsonArray(keys.Select(k =>
        {
            var jwk = new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["use"] = "sig",
                ["alg"] = "ES256",
                ["kid"] = k.Kid,
                ["x"] = EvidenceVerifier.Base64Url(k.X),
                ["y"] = EvidenceVerifier.Base64Url(k.Y),
                ["created_at"] = EvidenceHasher.FormatSignedAt(k.CreatedAt),
            };
            if (k.RetiredAt is { } r) jwk["retired_at"] = EvidenceHasher.FormatSignedAt(r);
            return (JsonNode)jwk;
        }).ToArray()),
    };

    public static IEndpointRouteBuilder MapEvidenceJwks(this IEndpointRouteBuilder app)
    {
        app.MapGet(JwksPath, async (IEvidenceKeyStore store, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Content(ToJwks(await store.GetPublicKeysAsync(ct)).ToJsonString(), "application/jwk-set+json");
        }).ExcludeFromDescription();
        return app;
    }
}
