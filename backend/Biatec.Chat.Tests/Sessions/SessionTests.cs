using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

namespace Biatec.Chat.Tests.Sessions;

/// <summary>Needs the docker-compose postgres. Each run migrates a fresh, uniquely named database.</summary>
public sealed class SessionTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

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
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat(), TenantSeed.LocalhostDev()));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private ChatDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, string? cookie = null, string host = "chat.biatec.io")
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(m, "http://" + host + path);
        r.Headers.TryAddWithoutValidation("Origin", host == "localhost" ? "http://localhost:5173" : Origin);
        if (m != HttpMethod.Get)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return await client.SendAsync(r);
    }

    private static string? SetCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.SingleOrDefault(x => x.StartsWith(SessionPolicy.CookieName + "=")) : null;

    private static string TokenOf(string setCookie) => setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];

    private async Task<string> NewSessionAsync()
    {
        var r = await SendAsync(HttpMethod.Get, "/session/probe");
        return TokenOf(SetCookie(r)!);
    }

    [Fact]
    public async Task Cookie_HasExactlyTheSpecifiedAttributes()
    {
        var r = await SendAsync(HttpMethod.Get, "/session");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var c = SetCookie(r)!;
        Assert.StartsWith("__Host-", c);
        var parts = c.Split(';').Select(p => p.Trim()).ToList();
        Assert.Contains("path=/", parts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("secure", parts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("httponly", parts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", parts, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(parts, p => p.StartsWith("domain", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Cookie_AttributesDoNotDriftOnLocalhostHttp()
    {
        var r = await SendAsync(HttpMethod.Get, "/session/probe", host: "localhost");
        // localhost dev tenant: same fixed attributes over plain http.
        var c = SetCookie(r);
        Assert.NotNull(c);
        Assert.Contains("secure", c!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", c, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_Is256BitsAndSessionIdIsNotTheCookie()
    {
        var token = await NewSessionAsync();
        Assert.Equal(43, token.Length); // 32 bytes base64url
        var r = await SendAsync(HttpMethod.Get, "/session", token);
        var state = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(token, state.GetRawText());
        Assert.Null(SetCookie(r));
        await using var db = NewContext();
        Assert.Equal(1, await db.ChatSessions.CountAsync());
        Assert.NotEqual(token, (await db.ChatSessions.SingleAsync()).Id.ToString());
    }

    [Fact]
    public async Task Probe_FirstCallSetsCookieAndReportsFalse_SecondWithCookieReportsTrue()
    {
        var first = await SendAsync(HttpMethod.Get, "/session/probe");
        Assert.False((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
        var token = TokenOf(SetCookie(first)!);

        var second = await SendAsync(HttpMethod.Get, "/session/probe", token);
        Assert.True((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
        Assert.Null(SetCookie(second));
    }

    [Fact]
    public async Task Probe_WithoutCookieAlwaysFalse()
    {
        for (var i = 0; i < 2; i++)
        {
            var r = await SendAsync(HttpMethod.Get, "/session/probe");
            Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
        }
    }

    [Fact]
    public async Task ForgedOrMalformedCookie_IsNotAdopted()
    {
        var r = await SendAsync(HttpMethod.Get, "/session/probe", "attacker-chosen-value");
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
        Assert.NotEqual("attacker-chosen-value", TokenOf(SetCookie(r)!));
        var wellFormed = new string('A', 43);
        var r2 = await SendAsync(HttpMethod.Get, "/session/probe", wellFormed);
        Assert.NotEqual(wellFormed, TokenOf(SetCookie(r2)!));
    }

    [Fact]
    public async Task Keepalive_ExtendsIdleButNeverAbsolute()
    {
        var token = await NewSessionAsync();
        await using var db = NewContext();
        var before = await db.ChatSessions.AsNoTracking().SingleAsync();

        _time.Now += TimeSpan.FromMinutes(10);
        var r = await SendAsync(HttpMethod.Post, "/session/keepalive", token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var after = await db.ChatSessions.AsNoTracking().SingleAsync();
        Assert.Equal(_time.Now, after.LastActivityAt);
        Assert.Equal(before.AbsoluteExpiresAt, after.AbsoluteExpiresAt);

        // 10 + 10 minutes after creation: alive only because the keepalive reset the idle clock.
        _time.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/session/keepalive", token)).StatusCode);
    }

    [Fact]
    public async Task Keepalive_CannotOutliveAbsoluteLifetime()
    {
        var token = await NewSessionAsync();
        // Keep the session idle-alive every 10 minutes for 8 hours.
        for (var i = 0; i < 47; i++)
        {
            _time.Now += TimeSpan.FromMinutes(10);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/session/keepalive", token)).StatusCode);
        }
        _time.Now += TimeSpan.FromMinutes(10); // 8h00 since creation
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/session/keepalive", token)).StatusCode);
    }

    [Fact]
    public async Task IdleExpiredSession_IsTreatedAsNewAnonymousSession()
    {
        var token = await NewSessionAsync();
        _time.Now += TimeSpan.FromMinutes(15);
        var r = await SendAsync(HttpMethod.Get, "/session/probe", token);
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
        Assert.NotEqual(token, TokenOf(SetCookie(r)!));
        // Keepalive on the expired session does not revive it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/session/keepalive", token)).StatusCode);
    }

    [Fact]
    public async Task JustInsideIdleWindow_IsStillLive()
    {
        var token = await NewSessionAsync();
        _time.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1);
        var r = await SendAsync(HttpMethod.Get, "/session/probe", token);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean());
    }

    /// <summary>Opens a consent request on the session's conversation (T-080). Status and expiry are the knobs.</summary>
    private async Task AddConsentAsync(string token, TimeSpan expiresIn, Core.Data.ConsentStatus status = Core.Data.ConsentStatus.Pending)
    {
        var now = _time.Now;
        await using var db = NewContext();
        var id = SessionService.IdFromToken(token);
        var conv = (await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == id)).ConversationId;
        var preview = Guid.NewGuid();
        var step = Guid.NewGuid();
        db.ActionPreviews.Add(new ActionPreview { Id = preview, ConversationId = conv, Capability = "dex.swap", CanonicalJson = "{}", ExpiresAt = now.AddMinutes(30), Status = ActionPreviewStatus.Confirmed });
        db.ActionSteps.Add(new ActionStep { Id = step, PreviewId = preview, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = "h", ExpiresAt = now.AddMinutes(30), Status = StepStatus.AwaitingConsent });
        db.ConsentRequests.Add(new ConsentRequest { Id = Guid.NewGuid(), StepId = step, SubjectHash = "h", AuthReqId = "ar-" + Guid.NewGuid(), ExpiresAt = now + expiresIn, Status = status });
        await db.SaveChangesAsync();
    }

    /// <summary>A multi-step preview (T-080) with no ConsentRequest: each step's status and expiry are given.</summary>
    private async Task AddPreviewAsync(string token, ActionPreviewStatus status, params (StepStatus Status, TimeSpan ExpiresIn)[] steps)
    {
        var now = _time.Now;
        await using var db = NewContext();
        var id = SessionService.IdFromToken(token);
        var conv = (await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == id)).ConversationId;
        var preview = Guid.NewGuid();
        db.ActionPreviews.Add(new ActionPreview { Id = preview, ConversationId = conv, Capability = "dex.liquidity", CanonicalJson = "{}", ExpiresAt = now + steps[0].ExpiresIn, Status = status });
        for (var i = 0; i < steps.Length; i++)
            db.ActionSteps.Add(new ActionStep { Id = Guid.NewGuid(), PreviewId = preview, Seq = i, Kind = ActionStepKind.Sign, SubjectHash = "h" + i, ExpiresAt = now + steps[i].ExpiresIn, Status = steps[i].Status });
        await db.SaveChangesAsync();
    }

    private async Task<bool> ProbeAsync(string token) =>
        (await (await SendAsync(HttpMethod.Get, "/session/probe", token)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cookieReturned").GetBoolean();

    [Fact]
    public async Task IdleSessionWithOpenConsent_StaysLivePastIdleTimeout()
    {
        // A fresh idle session per call: each call touches its session, which would mask the exemption for the next.
        var forState = await NewSessionAsync();
        var forProbe = await NewSessionAsync();
        var forKeepalive = await NewSessionAsync();
        foreach (var t in new[] { forState, forProbe, forKeepalive }) await AddConsentAsync(t, TimeSpan.FromMinutes(20));
        _time.Now += TimeSpan.FromMinutes(16); // a throttled tab: no keepalive for 16 minutes

        var r = await SendAsync(HttpMethod.Get, "/session", forState);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(SetCookie(r)); // the same session, not a new anonymous one
        Assert.True(await ProbeAsync(forProbe));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/session/keepalive", forKeepalive)).StatusCode);
        await using var db = NewContext();
        Assert.Equal(3, await db.ChatSessions.CountAsync());
    }

    [Fact]
    public async Task ConsentDeadline_HasAGracePeriod_ThenTheIdleTimeoutApplies()
    {
        var justPast = await NewSessionAsync();
        await AddConsentAsync(justPast, TimeSpan.FromMinutes(16));
        var pastGrace = await NewSessionAsync();
        await AddConsentAsync(pastGrace, TimeSpan.FromMinutes(16) - SessionPolicy.InFlightGrace - TimeSpan.FromSeconds(1));
        _time.Now += TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(1);

        // The poll right after the deadline still reaches the session, so it can record the timeout.
        Assert.True(await ProbeAsync(justPast));
        Assert.False(await ProbeAsync(pastGrace));
    }

    [Fact]
    public async Task MultiStepActionBetweenSteps_StaysLivePastIdleTimeout()
    {
        // Step 0 landed, step 1 not yet asked for consent: no ConsentRequest is Pending, but the action is in flight.
        var inFlight = await NewSessionAsync();
        await AddPreviewAsync(inFlight, ActionPreviewStatus.InProgress, (StepStatus.Confirmed, TimeSpan.FromMinutes(10)), (StepStatus.Pending, TimeSpan.FromMinutes(40)));
        var proposedOnly = await NewSessionAsync();
        await AddPreviewAsync(proposedOnly, ActionPreviewStatus.Proposed, (StepStatus.Pending, TimeSpan.FromMinutes(40)));
        var stepExpired = await NewSessionAsync();
        await AddPreviewAsync(stepExpired, ActionPreviewStatus.InProgress, (StepStatus.Confirmed, TimeSpan.FromMinutes(5)), (StepStatus.Pending, TimeSpan.FromMinutes(10)));
        _time.Now += TimeSpan.FromMinutes(16);

        Assert.True(await ProbeAsync(inFlight));
        Assert.False(await ProbeAsync(proposedOnly)); // a preview the user never confirmed does not keep a session alive
        Assert.False(await ProbeAsync(stepExpired));
    }

    [Fact]
    public async Task IdleSessionWithOpenConsent_ReachesConsentEndpoint()
    {
        var token = await NewSessionAsync();
        await using (var db = NewContext())
            await db.ChatSessions.ExecuteUpdateAsync(u => u.SetProperty(x => x.Tier, Core.Data.SessionTier.Authenticated));
        await AddConsentAsync(token, TimeSpan.FromMinutes(20));
        _time.Now += TimeSpan.FromMinutes(16);

        // FindLiveAsync accepts the session: whatever the poll answers for an unknown preview, it is not 401.
        var r = await SendAsync(HttpMethod.Post, $"/chat/previews/{Guid.NewGuid()}/consent", token);
        Assert.NotEqual(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task IdleSessionWithOpenConsent_CanRotate()
    {
        var token = await NewSessionAsync();
        await AddConsentAsync(token, TimeSpan.FromMinutes(20));
        _time.Now += TimeSpan.FromMinutes(16);
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.NotNull(await svc.RotateAsync(TenantSeed.BiatecChat(), token, default));
    }

    [Fact]
    public async Task OpenConsent_NeverExtendsAbsoluteLifetime()
    {
        var token = await NewSessionAsync();
        await using (var db = NewContext())
            await db.ChatSessions.ExecuteUpdateAsync(u => u.SetProperty(x => x.AbsoluteExpiresAt, _time.Now.AddMinutes(10)));
        await AddConsentAsync(token, TimeSpan.FromMinutes(30));
        _time.Now += TimeSpan.FromMinutes(10);

        Assert.False(await ProbeAsync(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/session/keepalive", token)).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ISessionService>().RotateAsync(TenantSeed.BiatecChat(), token, default));
    }

    [Fact]
    public async Task ClosedOrExpiredConsent_DoesNotExemptFromIdleTimeout()
    {
        var expired = await NewSessionAsync();
        await AddConsentAsync(expired, TimeSpan.FromMinutes(5));
        var approved = await NewSessionAsync();
        await AddConsentAsync(approved, TimeSpan.FromMinutes(20), Core.Data.ConsentStatus.Approved);
        _time.Now += TimeSpan.FromMinutes(15);

        Assert.False(await ProbeAsync(expired));
        Assert.False(await ProbeAsync(approved));
    }

    [Fact]
    public async Task State_ReportsAnonymousTierAndAbsoluteExpiry()
    {
        var r = await SendAsync(HttpMethod.Get, "/session");
        var s = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anonymous", s.GetProperty("tier").GetString());
        Assert.Equal("None", s.GetProperty("signInStatus").GetString());
        Assert.Equal(_time.Now + TimeSpan.FromHours(8), s.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(s.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Rotate_IssuesNewIdKeepsConversationAndAbsoluteExpiry_OldTokenDies()
    {
        var token = await NewSessionAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var tenant = TenantSeed.BiatecChat();
        await using var db = NewContext();
        var before = await db.ChatSessions.AsNoTracking().SingleAsync();

        _time.Now += TimeSpan.FromMinutes(5);
        var rotated = await svc.RotateAsync(tenant, token, default);
        Assert.NotNull(rotated);
        Assert.NotEqual(token, rotated!.NewToken);
        Assert.Equal(before.ConversationId, rotated.Session.ConversationId);
        Assert.Equal(before.AbsoluteExpiresAt, rotated.Session.AbsoluteExpiresAt);
        Assert.Equal(1, await db.ChatSessions.CountAsync());

        Assert.Null(await svc.RotateAsync(tenant, token, default)); // old token no longer live
        Assert.False((await (await SendAsync(HttpMethod.Get, "/session/probe", token)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cookieReturned").GetBoolean());
        Assert.True((await (await SendAsync(HttpMethod.Get, "/session/probe", rotated.NewToken)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cookieReturned").GetBoolean());
    }

    [Fact]
    public async Task Keepalive_NeedsCsrfHeader()
    {
        var token = await NewSessionAsync();
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/session/keepalive");
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={token}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(r)).StatusCode);
    }

    [Fact]
    public async Task UnregisteredOrigin_GetsNoSessionAndNoCookie()
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Get, "http://chat.biatec.io/session/probe");
        r.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        var res = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Null(SetCookie(res));
        await using var db = NewContext();
        Assert.Equal(0, await db.ChatSessions.CountAsync());
    }
}
