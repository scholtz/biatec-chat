using Biatec.Chat.Api.Agent;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Auth.Elevation;

public sealed record ElevatePopupRequest
{
    public string? Scope { get; init; }
}

public static class ElevationRegistration
{
    public static IServiceCollection AddScopeElevation(this IServiceCollection services)
    {
        services.AddSingleton<IScopeGuard, ScopeGuard>();
        return services;
    }
}

/// <summary>
/// Incremental consent in the same popup mechanism as sign-in: POST /auth/elevate/popup (from script) creates the
/// request and its one-time nonce, GET /auth/elevate (the popup navigation) redirects to Biatec OIDC asking for the
/// union of the held scopes and the new one. The callback (AuthEndpoints) completes it via <see cref="ElevationCompletion"/>.
/// </summary>
public static class ElevationEndpoints
{
    public const string Purpose = "elevate";
    private static readonly TimeSpan PopupLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapElevation(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/elevate/popup", async (ElevatePopupRequest? body, HttpContext ctx, ChatDbContext db, TimeProvider clock, ITokenVault vault) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            var now = clock.GetUtcNow();
            var scope = body?.Scope;
            if (!ElevationScopes.IsAllowed(scope))
                return Results.BadRequest(new { error = "scope_not_allowed" });
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), now, ctx.RequestAborted);
            if (session is null || session.Tier != SessionTier.Authenticated || await vault.GetAsync(session.Id.ToString("N"), ctx.RequestAborted) is null)
                return Results.Unauthorized();
            if (RestrictionFor(session, scope!) is { } refusal)
                return Results.Json(new { error = refusal.Code, message = refusal.Message }, statusCode: StatusCodes.Status403Forbidden);

            await db.PopupAuthRequests
                .Where(p => p.ConversationId == session.ConversationId && p.Purpose == PopupAuthPurpose.Elevate && p.Status == PopupAuthStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PopupAuthStatus.Expired), ctx.RequestAborted);

            var nonce = AuthEndpoints.Random64();
            var req = new PopupAuthRequest
            {
                Id = Guid.NewGuid(),
                ConversationId = session.ConversationId,
                Purpose = PopupAuthPurpose.Elevate,
                NonceHash = NonceHash(scope!, nonce),
                Status = PopupAuthStatus.Pending,
                ExpiresAt = now + PopupLifetime,
            };
            db.PopupAuthRequests.Add(req);
            await db.SaveChangesAsync(ctx.RequestAborted);

            var origin = Biatec.Chat.Api.Hosting.BackendUrls.Base(tenant, ctx.Request.PathBase.Value ?? "");
            return Results.Ok(new PopupStartResponse
            {
                AuthRequestId = req.Id.ToString(),
                LoginUrl = $"{origin}/auth/elevate?req={req.Id}&nonce={Uri.EscapeDataString(nonce)}&scope={Uri.EscapeDataString(scope!)}",
            });
        }).WithName("StartElevationPopup").Produces<PopupStartResponse>();

        app.MapGet("/auth/elevate", async (HttpContext ctx, ChatDbContext db, TimeProvider clock, IOidcClient oidc, IAuthStateStore states,
            ITokenVault vault, ILogger<AuthEndpoints.AuthLog> log) =>
        {
            Harden(ctx);
            var tenant = ctx.GetTenant()!;
            if (!AuthEndpoints.NavigationAllowed(ctx.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var scope = ctx.Request.Query["scope"].ToString();
            if (!ElevationScopes.IsAllowed(scope)) return AuthEndpoints.ClosePage(ctx, 400); // checked before anything is consumed
            var now = clock.GetUtcNow();
            var session = await AuthEndpoints.FindLiveAsync(db, tenant, SessionEndpoints.ReadCookie(ctx.Request), now, ctx.RequestAborted);
            var nonce = ctx.Request.Query["nonce"].ToString();
            if (session is null || session.Tier != SessionTier.Authenticated || !Guid.TryParse(ctx.Request.Query["req"], out var reqId) || nonce.Length is 0 or > 128)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (RestrictionFor(session, scope) is not null) return AuthEndpoints.ClosePage(ctx, 403);

            var hash = NonceHash(scope, nonce);
            var used = await db.PopupAuthRequests
                .Where(p => p.Id == reqId && p.ConversationId == session.ConversationId && p.Purpose == PopupAuthPurpose.Elevate
                            && p.Status == PopupAuthStatus.Pending && p.ExpiresAt > now && p.NonceHash == hash)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.NonceHash, "consumed"), ctx.RequestAborted);
            if (used != 1) return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                oidc.EnsureConfigured(tenant);
                var entry = await vault.GetAsync(session.Id.ToString("N"), ctx.RequestAborted)
                    ?? throw new OidcException("No tokens are held for this session.");
                var requested = string.Join(' ', ElevationScopes.Union(
                    entry.Scopes.Where(s => ElevationScopes.IsAllowed(s) || AuthEndpoints.LoginScope.Split(' ').Contains(s)), scope));
                var state = AuthEndpoints.Random64();
                var oidcNonce = AuthEndpoints.Random64();
                var verifier = AuthEndpoints.Random64();
                await states.PutAsync(AuthEndpoints.Hash(state), new PendingAuth
                {
                    SessionId = session.Id.ToString("N"),
                    TenantId = tenant.Id,
                    PopupId = reqId,
                    Purpose = Purpose,
                    Nonce = oidcNonce,
                    Verifier = verifier,
                    Scope = requested,
                    Elevate = scope,
                }, StateLifetime, ctx.RequestAborted);
                var url = await oidc.BuildAuthorizeUrlAsync(tenant, requested, state, oidcNonce, AuthEndpoints.Challenge(verifier), ctx.RequestAborted);
                return Results.Redirect(url.AbsoluteUri);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Could not start scope elevation: {ErrorType}", ex.GetType().Name);
                await AuthEndpoints.SetStatusAsync(db, reqId, session.ConversationId, PopupAuthStatus.Failed, ctx.RequestAborted);
                return AuthEndpoints.ClosePage(ctx, 503);
            }
        }).WithName("AuthElevate");

        return app;
    }

    /// <summary>Paused or analyze sessions may still elevate manage-limits (a lowering-only path); sign stays refused.</summary>
    private static AgentRefusal? RestrictionFor(ChatSession session, string scope) =>
        scope == ElevationScopes.ManageLimits ? null : AgentModePolicy.ConfirmRefusal(session);

    private static string NonceHash(string scope, string nonce) => AuthEndpoints.Hash(scope + "\n" + nonce);

    private static void Harden(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Append("Vary", "Cookie");
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}

/// <summary>The callback half of an elevation: exchange, verify, vault the new tokens, replace the entry.</summary>
public static class ElevationCompletion
{
    /// <summary>
    /// Returns true when the new tokens are vaulted (the old entry replaced). Any failure leaves the existing entry
    /// untouched. The new tokens are stored first; revoking the old refresh token waits for the RFC 7009 endpoint at
    /// Biatec OIDC (docs/required-changes-biatec-oidc.md), so it simply ages out.
    /// </summary>
    public static async Task<bool> CompleteAsync(Tenant tenant, ChatSession session, PendingAuth pending, string code, TimeProvider clock,
        IOidcClient oidc, IIdTokenValidator validator, ITokenVault vault, CancellationToken ct)
    {
        var requestedScope = pending.Elevate;
        if (!ElevationScopes.IsAllowed(requestedScope)) return false;
        var sessionId = session.Id.ToString("N");
        var old = await vault.GetAsync(sessionId, ct);
        if (old is null || session.Tier != SessionTier.Authenticated) return false;

        var tokens = await oidc.ExchangeCodeAsync(tenant, code, pending.Verifier, ct);
        if (string.IsNullOrEmpty(tokens.IdToken) || string.IsNullOrEmpty(tokens.RefreshToken)) return false;
        var identity = await validator.ValidateAsync(tokens.IdToken, tenant.OidcClientId, pending.Nonce, ct);
        // The consent must have been given by the account that owns this session, never another one.
        if (!string.Equals(identity.Subject, session.OidcSub, StringComparison.Ordinal)) return false;

        var granted = tokens.Scopes ?? pending.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The new scope must really have been granted (the user may have declined it).
        if (Array.IndexOf(granted, requestedScope!) < 0) return false;
        // Scopes only grow within a session: a response that drops an allow-listed scope already held is refused,
        // so signing is never silently lost by a later elevation.
        foreach (var held in old.Scopes)
            if (ElevationScopes.IsAllowed(held) && Array.IndexOf(granted, held) < 0) return false;

        // The exchange took a network round trip: the session may have been logged out meanwhile, so re-read the
        // entry and never re-create tokens for an ended session.
        old = await vault.GetAsync(sessionId, ct);
        if (old is null || clock.GetUtcNow() >= old.AbsoluteExpiresAt) return false;

        await vault.PutAsync(sessionId, new TokenVaultEntry
        {
            AccessToken = tokens.AccessToken,
            AccessExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn),
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            Scopes = granted, // exactly what the new access token carries, so the scope check matches the token
            AbsoluteExpiresAt = old.AbsoluteExpiresAt, // elevation never extends the session
        }, ct);
        return true;
    }
}
