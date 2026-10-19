using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Health;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;

if (args is [BedrockSmoke.Command, .. var smokeArgs])
    return await BedrockSmoke.RunAsync(smokeArgs);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBiatecOptions(builder.Configuration);
builder.Services.AddBiatecHealth();
builder.Services.AddContractsOpenApi();
builder.Services.AddBiatecData();
builder.Services.AddTokenVault(builder.Configuration);
builder.Services.AddTenancy();
builder.Services.AddSessions();
builder.Services.AddBedrockModel();

var app = builder.Build();
await app.MigrateDevelopmentDatabaseAsync();
await app.SeedTenantsAsync();

app.UseTenancy();

// 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
app.MapHealthChecks("/health");

app.MapContractCatalog();
app.MapSessions();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
return 0;

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program;
