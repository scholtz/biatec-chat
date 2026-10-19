using System.Text.Json.Nodes;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Api.Evidence.Jwks;

public sealed class EvidenceOptions
{
    public const string Section = "Evidence";

    /// <summary>Optional file for the local dev signing keys (so they survive restarts). Never a production setting.</summary>
    public string? DevKeyFile { get; set; }
}

public static class EvidenceJwks
{
    public const string Path = "/.well-known/evidence-jwks.json";

    /// <summary>Registers the evidence log and the dev signing key store (register a production <see cref="IEvidenceKeyStore"/> first to replace it).</summary>
    public static IServiceCollection AddEvidence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EvidenceOptions>(config.GetSection(EvidenceOptions.Section));
        services.TryAddSingleton<IEvidenceKeyStore>(sp =>
        {
            var o = config.GetSection(EvidenceOptions.Section).Get<EvidenceOptions>() ?? new EvidenceOptions();
            if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Evidence").LogWarning("Evidence signing uses the local Dev key store; register a production IEvidenceKeyStore.");
            return new DevEvidenceKeyStore(o.DevKeyFile);
        });
        services.TryAddScoped<IEvidenceLog>(sp => new EvidenceLog(sp.GetRequiredService<ChatDbContext>(), sp.GetRequiredService<IEvidenceKeyStore>()));
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
        app.MapGet(Path, async (IEvidenceKeyStore store, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Content(ToJwks(await store.GetPublicKeysAsync(ct)).ToJsonString(), "application/jwk-set+json");
        }).ExcludeFromDescription();
        return app;
    }
}
