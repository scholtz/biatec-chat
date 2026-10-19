using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Api.Tenancy;

/// <summary>The built-in tenants. Secrets are pointers into the secret store, never values.</summary>
public static class TenantSeed
{
    public static Tenant BiatecChat() => new()
    {
        Id = "biatec-chat",
        Host = "chat.biatec.io",
        SignInPageUrl = "https://web.biatec.io",
        OidcClientId = "biatec-chat",
        ClientSecretRef = "secret://biatec-chat/oidc-client-secret",
        RedirectUri = "https://chat.biatec.io/auth/callback",
        AllowedOrigins = ["https://web.biatec.io", "https://dex.biatec.io"],
        ClientKeys = "[\"biatec-web\",\"biatec-dex\"]",
        ConsentThresholdCeilingEur = 1000,
        SoftCapEurPerHour = 1500,
        TrustAnchorVerified = true,
    };

    public static Tenant LocalhostDev() => new()
    {
        Id = "localhost-dev",
        Host = "localhost",
        SignInPageUrl = "http://localhost:5173",
        OidcClientId = "biatec-chat-dev",
        ClientSecretRef = "secret://biatec-chat/dev-oidc-client-secret",
        RedirectUri = "http://localhost/auth/callback",
        AllowedOrigins = ["http://localhost:5173", "http://localhost:3000"],
        ClientKeys = "[\"dev\"]",
        ConsentThresholdCeilingEur = 1000,
        SoftCapEurPerHour = 1500,
    };

    /// <summary>Inserts missing seed tenants. Development only (other environments seed as a deployment step).</summary>
    public static async Task SeedTenantsAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        foreach (var t in new[] { BiatecChat(), LocalhostDev() })
        {
            if (!await db.Tenants.AnyAsync(x => x.Host == t.Host)) db.Tenants.Add(t);
        }
        await db.SaveChangesAsync();
    }
}
