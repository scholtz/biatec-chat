using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Chat.Cancel;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Cancel;

/// <summary>T-075: POST /chat/previews/{id}/cancel. Needs the docker-compose postgres.</summary>
public sealed class CancelEndpointTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private static readonly string Hash = new('a', 64);
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://oidc.example.test",
                ["Oidc:McpResource"] = "https://mcp.example.test",
                ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                ["Redis:ConnectionString"] = "127.0.0.1:1",
                ["Postgres:ConnectionString"] = Cs,
                ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                ["Kms:KeyId"] = "alias/test",
                ["Evidence:AllowEphemeralKeys"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private HttpRequestMessage Req(HttpMethod m, string path, string? cookie, bool csrf = true, object? body = null)
    {
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        if (csrf)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return r;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(r);
    }

    private async Task<(string Cookie, Guid Id, Guid ConversationId)> NewSessionAsync(bool authenticated)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/session", null));
        var sc = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        var token = sc[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];
        var id = SessionService.IdFromToken(token);
        await using var db = NewContext();
        if (authenticated)
            await db.ChatSessions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Tier, SessionTier.Authenticated));
        var conv = (await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == id)).ConversationId;
        return (token, id, conv);
    }

    private async Task<(Guid PreviewId, Guid StepId)> SeedAsync(Guid conversationId, ActionPreviewStatus pStatus = ActionPreviewStatus.Proposed,
        StepStatus sStatus = StepStatus.Pending, TimeSpan? expiresIn = null)
    {
        await using var db = NewContext();
        var exp = DateTimeOffset.UtcNow + (expiresIn ?? TimeSpan.FromMinutes(20));
        var preview = new ActionPreview
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Capability = "wallet.transfer", CanonicalJson = "{}",
            Status = pStatus, ExpiresAt = exp,
        };
        var step = new ActionStep
        {
            Id = Guid.NewGuid(), PreviewId = preview.Id, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = Hash,
            Network = "testnet", ExpiresAt = exp, Status = sStatus,
        };
        db.ActionPreviews.Add(preview);
        db.ActionSteps.Add(step);
        await db.SaveChangesAsync();
        return (preview.Id, step.Id);
    }

    private Task<HttpResponseMessage> CancelAsync(string? cookie, Guid previewId, string? hash = null, bool csrf = true) =>
        SendAsync(Req(HttpMethod.Post, $"/chat/previews/{previewId}/cancel", cookie, csrf, new { groupHash = hash ?? Hash }));

    [Fact]
    public async Task Cancel_OfAnOpenPreview_CancelsItAndItsStepsAndConfirmIsRefused()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, sid) = await SeedAsync(conv);

        var r = await CancelAsync(cookie, pid);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("cancelled", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        await using var db = NewContext();
        Assert.Equal(ActionPreviewStatus.Cancelled, (await db.ActionPreviews.SingleAsync(p => p.Id == pid)).Status);
        Assert.Equal(StepStatus.Cancelled, (await db.ActionSteps.SingleAsync(s => s.Id == sid)).Status);

        var confirm = await SendAsync(Req(HttpMethod.Post, $"/chat/previews/{pid}/confirm", cookie, body: new { groupHash = Hash }));
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal(StepStatus.Cancelled, (await db.ActionSteps.AsNoTracking().SingleAsync(s => s.Id == sid)).Status);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_ForAnAlreadyCancelledPreview()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(cookie, pid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(cookie, pid)).StatusCode);
        await using var db = NewContext();
        Assert.Equal(1, await db.EvidenceEntries.CountAsync(e => e.ConversationId == conv && e.Kind == CancelService.EvidenceKind));
    }

    [Theory]
    [InlineData(ActionPreviewStatus.Completed, StepStatus.Confirmed)]
    [InlineData(ActionPreviewStatus.Failed, StepStatus.Failed)]
    [InlineData(ActionPreviewStatus.Expired, StepStatus.Expired)]
    public async Task Cancel_OfACompletedFailedOrExpiredPreview_Is409(ActionPreviewStatus p, StepStatus s)
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv, p, s);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid)).StatusCode);
        await using var db = NewContext();
        Assert.Equal(p, (await db.ActionPreviews.SingleAsync(x => x.Id == pid)).Status);
    }

    [Fact]
    public async Task Cancel_OfAPreviewWhoseWindowHasLapsed_Is409()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv, expiresIn: TimeSpan.FromMinutes(-1));
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid)).StatusCode);
    }

    [Fact]
    public async Task Cancel_OfASubmittedStep_Is409()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv, ActionPreviewStatus.InProgress, StepStatus.Submitted);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid)).StatusCode);
    }

    [Fact]
    public async Task Cancel_WithAWrongGroupHash_Is409AndChangesNothing()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid, new string('b', 64))).StatusCode);
        await using var db = NewContext();
        Assert.Equal(ActionPreviewStatus.Proposed, (await db.ActionPreviews.SingleAsync(x => x.Id == pid)).Status);
    }

    [Fact]
    public async Task Cancel_Anonymous_NoSession_OrAnotherConversation_Is401()
    {
        var (anon, _, anonConv) = await NewSessionAsync(false);
        var (pidAnon, _) = await SeedAsync(anonConv);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CancelAsync(anon, pidAnon)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CancelAsync(null, pidAnon)).StatusCode);

        var (_, _, ownerConv) = await NewSessionAsync(true);
        var (other, _, _) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(ownerConv);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CancelAsync(other, pid)).StatusCode);
        await using var db = NewContext();
        Assert.Equal(ActionPreviewStatus.Proposed, (await db.ActionPreviews.SingleAsync(x => x.Id == pid)).Status);
    }

    [Fact]
    public async Task Cancel_OfAnUnknownPreview_Is404()
    {
        var (cookie, _, _) = await NewSessionAsync(true);
        Assert.Equal(HttpStatusCode.NotFound, (await CancelAsync(cookie, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Cancel_WithoutTheCsrfHeaders_IsForbidden()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv);
        Assert.Equal(HttpStatusCode.Forbidden, (await CancelAsync(cookie, pid, csrf: false)).StatusCode);
    }

    [Theory]
    [InlineData("analyze", false)]
    [InlineData("prepare", true)]
    public async Task Cancel_WorksWhileAnalyzeOnlyOrPaused(string mode, bool paused)
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Req(HttpMethod.Put, "/session/agent", cookie, body: new { mode, paused }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(cookie, pid)).StatusCode);
    }

    [Fact]
    public async Task Cancel_OfAPreviewAwaitingConsent_CancelsTheConsentRequestAndWritesEvidence()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, sid) = await SeedAsync(conv, ActionPreviewStatus.Confirmed, StepStatus.AwaitingConsent);
        var consentId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.ConsentRequests.Add(new ConsentRequest
            {
                Id = consentId, StepId = sid, SubjectHash = Hash, AuthReqId = "req-1", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ConsentStatus.Pending,
            });
            await seed.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await CancelAsync(cookie, pid)).StatusCode);

        await using var db = NewContext();
        Assert.Equal(ConsentStatus.Cancelled, (await db.ConsentRequests.SingleAsync(c => c.Id == consentId)).Status);
        Assert.Equal(StepStatus.Cancelled, (await db.ActionSteps.SingleAsync(s => s.Id == sid)).Status);
        Assert.Equal(1, await db.EvidenceEntries.CountAsync(e => e.ConversationId == conv && e.Kind == CancelService.EvidenceKind));
    }

    [Fact]
    public async Task Cancel_OfAPreviewWhoseConsentWasApproved_Is409AndChangesNothing()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, sid) = await SeedAsync(conv, ActionPreviewStatus.Confirmed, StepStatus.AwaitingConsent);
        await using (var seed = NewContext())
        {
            seed.ConsentRequests.Add(new ConsentRequest
            {
                Id = Guid.NewGuid(), StepId = sid, SubjectHash = Hash, AuthReqId = "req-2", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Status = ConsentStatus.Approved,
            });
            await seed.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid)).StatusCode);

        await using var db = NewContext();
        Assert.Equal(StepStatus.AwaitingConsent, (await db.ActionSteps.SingleAsync(s => s.Id == sid)).Status);
        Assert.Equal(ActionPreviewStatus.Confirmed, (await db.ActionPreviews.SingleAsync(x => x.Id == pid)).Status);
    }

    [Fact]
    public async Task Cancel_WhileSigningIsInFlight_Is409()
    {
        var (cookie, _, conv) = await NewSessionAsync(true);
        var (pid, _) = await SeedAsync(conv, ActionPreviewStatus.Confirmed, StepStatus.Pending);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(cookie, pid)).StatusCode);
    }
}
