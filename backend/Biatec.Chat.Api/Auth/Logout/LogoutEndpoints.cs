using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Sweeper;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Auth.Logout;

public static class LogoutRegistration
{
    public static IServiceCollection AddLogoutAndSweeper(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RevocationOptions>(config.GetSection(RevocationOptions.Section));
        services.Configure<SweeperOptions>(config.GetSection(SweeperOptions.Section));
        services.AddScoped<IRevocationService, RevocationService>();
        services.AddScoped<SessionSweeper>();
        services.AddHostedService<SweeperHostedService>();
        return services;
    }
}

public static class LogoutEndpoints
{
    /// <summary>
    /// GET /auth/logout is a top-level navigation, like /auth/login: the ID token must reach the browser only as the
    /// <c>id_token_hint</c> of the redirect to the end-session endpoint (the spec's RP-initiated logout), never in a
    /// body a script can read. Cross-site and subresource requests are refused through Sec-Fetch headers.
    /// Order: read the tokens, revoke the refresh token (or park it), delete the vault entry and the session,
    /// clear the cookie, redirect. Idempotent; with no signed-in session there is nothing to redirect to (204).
    /// </summary>
    public static IEndpointRouteBuilder MapLogout(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/logout", async (HttpContext ctx, ChatDbContext db, ITokenVault vault, IRevocationService revocation,
            IOidcMetadataProvider metadata, ILogger<AuthEndpoints.AuthLog> log) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Append("Vary", "Cookie");
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!AuthEndpoints.NavigationAllowed(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var tenant = ctx.GetTenant()!;
            var token = SessionEndpoints.ReadCookie(ctx.Request);
            string? idToken = null;

            if (SessionService.WellFormed(token))
            {
                var id = SessionService.IdFromToken(token!);
                var exists = await db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == id && s.TenantId == tenant.Id, ctx.RequestAborted);
                if (exists)
                {
                    var vaultId = id.ToString("N");
                    var entry = await vault.GetAsync(vaultId, ctx.RequestAborted);
                    if (entry is not null)
                    {
                        idToken = entry.IdToken;
                        try { await revocation.RevokeOrParkAsync(tenant, entry.RefreshToken, ctx.RequestAborted); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Not even parked: the fallback is to destroy the entry and let the token age out.
                            log.LogError("A refresh token could be neither revoked nor parked: {ErrorType}", ex.GetType().Name);
                        }
                    }
                    await vault.DeleteAsync(vaultId, ctx.RequestAborted);
                    await db.ChatSessions.Where(s => s.Id == id).ExecuteDeleteAsync(ctx.RequestAborted);
                }
            }

            ctx.Response.Cookies.Delete(SessionPolicy.CookieName, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", Domain = null,
            });

            if (string.IsNullOrEmpty(idToken)) return Results.NoContent();
            try
            {
                var meta = await metadata.GetAsync(ctx.RequestAborted);
                var q = $"id_token_hint={Uri.EscapeDataString(idToken)}&client_id={Uri.EscapeDataString(tenant.OidcClientId)}";
                return Results.Redirect(new UriBuilder(meta.EndSessionEndpoint) { Query = q }.Uri.AbsoluteUri);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not build the end-session URL: {ErrorType}", ex.GetType().Name);
                return Results.NoContent();
            }
        }).WithName("Logout");

        return app;
    }
}
