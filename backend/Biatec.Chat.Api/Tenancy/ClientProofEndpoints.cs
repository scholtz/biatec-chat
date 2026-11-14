using System.Text.Json;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Tenancy;

/// <summary>
/// The domain proof (decision D13): Biatec OIDC and the authenticator fetch <c>/.well-known/biatec-client.json</c> from the tenant's own host and
/// show that host as the proven domain when the client id they know is listed. Public by design: no authentication and nothing but the client id,
/// which is not a secret. The tenant comes from the Host header like everywhere else (<see cref="TenantMiddleware"/> answers 404 for an unknown host).
/// </summary>
public static class ClientProofEndpoints
{
    public const string Path = "/.well-known/biatec-client.json";

    public static IEndpointRouteBuilder MapClientProof(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (HttpContext ctx) =>
        {
            var tenant = ctx.GetTenant();
            if (tenant is null || string.IsNullOrWhiteSpace(tenant.OidcClientId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(new { client_ids = new[] { tenant.OidcClientId } }), "application/json");
        }).WithName("BiatecClientProof");
        return app;
    }
}
