using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Auth.Logout;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Anchoring;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Health;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBiatecOptions(builder.Configuration);
builder.Services.AddBiatecHealth();
builder.Services.AddContractsOpenApi();
builder.Services.AddBiatecData();
builder.Services.AddOidcAuth();
builder.Services.AddTokenVault(builder.Configuration);
builder.Services.AddTenancy();
builder.Services.AddSessions();
builder.Services.AddOnboarding(builder.Configuration);

builder.Services.AddScopeElevation();
builder.Services.AddLogoutAndSweeper(builder.Configuration);
builder.Services.AddEvidence(builder.Configuration);
builder.Services.AddEvidenceAnchoring(builder.Configuration);

var app = builder.Build();
await app.MigrateDevelopmentDatabaseAsync();
await app.SeedTenantsAsync();

app.UseTenancy();
app.UseOnboardingBodyLimit();
app.UseRateLimiter();

// 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
app.MapHealthChecks("/health");

app.MapContractCatalog();
app.MapSessions();
app.MapAuth();
app.MapOnboarding();

app.MapElevation();
app.MapLogout();
app.MapEvidenceJwks();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program;
