using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
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

var app = builder.Build();
await app.MigrateDevelopmentDatabaseAsync();
await app.SeedTenantsAsync();

app.UseTenancy();

// 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
app.MapHealthChecks("/health");

app.MapContractCatalog();
app.MapSessions();
app.MapAuth();
app.MapOnboarding();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program;
