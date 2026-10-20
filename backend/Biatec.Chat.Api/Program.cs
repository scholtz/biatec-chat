using Biatec.Chat.Api.Agent;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Auth.Logout;
using Biatec.Chat.Api.Chat;
using Biatec.Chat.Api.Chat.Cancel;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Consent;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Anchoring;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Evidence.Recording;
using Biatec.Chat.Api.Health;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Onboarding;
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
builder.Services.AddOidcAuth();
builder.Services.AddTokenVault(builder.Configuration);
builder.Services.AddTenancy();
builder.Services.AddSessions();
if (builder.Configuration.GetValue<TimeSpan?>("Sessions:InFlightGrace") is { } inFlightGrace)
    Biatec.Chat.Api.Sessions.SessionPolicy.InFlightGrace = inFlightGrace;
builder.Services.AddBedrockModel();
Biatec.Chat.Api.Budget.BudgetRegistration.AddBedrockBudget(builder.Services);
builder.Services.AddChat();
Biatec.Chat.Api.Chat.SignIn.AnonymousTierRegistration.AddAnonymousTier(builder.Services, builder.Configuration);
builder.Services.AddOnboarding(builder.Configuration);
Biatec.Chat.Api.Public.PublicChatEndpoints.AddPublicChat(builder.Services, builder.Configuration);
Biatec.Chat.Api.Public.ClientAddress.AddClientAddress(builder.Services, builder.Configuration);

builder.Services.AddScopeElevation();
builder.Services.AddConfirm();
Biatec.Chat.Api.Caps.CapsRegistration.AddSpendCaps(builder.Services);
builder.Services.AddCancel();
builder.Services.AddConsent();
builder.Services.AddLogoutAndSweeper(builder.Configuration);
builder.Services.AddEvidence(builder.Configuration);
builder.Services.AddEvidenceAnchoring(builder.Configuration);
builder.Services.AddEvidenceRecording();
Biatec.Chat.Api.Retention.RetentionRegistration.AddRetention(builder.Services);
builder.Services.AddSingleton<Biatec.Chat.Tools.Mcp.IMcpBearerSource, Biatec.Chat.Api.Mcp.VaultMcpBearerSource>();
Biatec.Chat.Tools.Mcp.McpRegistration.AddBiatecMcp(builder.Services, builder.Configuration);
Biatec.Chat.Api.Wallet.Read.WalletReadRegistration.AddWalletReadTools(builder.Services);
Biatec.Chat.Api.Wallet.Transfer.WalletTransferRegistration.AddWalletTransferTools(builder.Services);
Biatec.Chat.Api.Limits.LimitsRegistration.AddLimitsTools(builder.Services);
Biatec.Chat.Api.X402.X402Registration.AddX402Pay(builder.Services, builder.Configuration);
Biatec.Chat.Api.Dex.Liquidity.DexLiquidityRegistration.AddDexLiquidityTools(builder.Services, builder.Configuration);
Biatec.Chat.Api.Safe.Create.SafeCreateRegistration.AddSafeCreateTools(builder.Services, builder.Configuration);
Biatec.Chat.Api.Safe.Proposals.SafeProposalRegistration.AddSafeProposalTools(builder.Services, builder.Configuration);
// Last: it decorates services registered above.
Biatec.Chat.Api.Telemetry.TelemetryRegistration.AddBiatecTelemetry(builder.Services, builder.Configuration);

var app = builder.Build();
await app.MigrateDevelopmentDatabaseAsync();
await app.SeedTenantsAsync();

Biatec.Chat.Api.Public.ClientAddress.UseClientAddress(app);
app.UseTenancy();
app.UseOnboardingBodyLimit();
app.UseRateLimiter();

// 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
app.MapHealthChecks("/health");
// Liveness only: runs no dependency checks, so a Redis or PostgreSQL outage never restarts the pods.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });

app.MapContractCatalog();
app.MapSessions();
app.MapAgentMode();
app.MapChat();
Biatec.Chat.Api.Public.PublicChatEndpoints.MapPublic(app);
app.MapAuth();
app.MapOnboarding();

app.MapElevation();
app.MapConfirm();
app.MapCancel();
app.MapConsent();
app.MapLogout();
app.MapEvidenceJwks();
Biatec.Chat.Api.Evidence.Export.EvidenceExportEndpoints.MapEvidenceExport(app);
Biatec.Chat.Api.Retention.RetentionEndpoints.MapRetention(app);
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
return 0;

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program;
