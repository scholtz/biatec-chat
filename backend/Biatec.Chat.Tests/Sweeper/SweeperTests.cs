using System.Net;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Logout;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Sweeper;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Biatec.Chat.Tests.Sweeper;

/// <summary>Needs the docker-compose postgres. Vault, clock and the OIDC revocation endpoint are fakes.</summary>
public sealed class SweeperTests : IAsyncLifetime
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public bool FailReads, CorruptReads, FailDeletes;
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) =>
            CorruptReads ? throw new System.Security.Cryptography.CryptographicException("bad") :
            FailReads ? throw new InvalidOperationException("redis down") : Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { if (FailDeletes) throw new InvalidOperationException("redis down"); Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private sealed class RevocationEndpoint : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly List<(string Auth, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Headers.Authorization?.ToString() ?? "", await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status);
        }
    }

    private const string RevocationUrl = "https://oidc.example.test/connect/revocation";
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private readonly MemoryVault _vault = new();
    private readonly RevocationEndpoint _oidc = new();
    private readonly DevKeyProvider _keys = new();
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
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        db.Tenants.Add(TenantSeed.BiatecChat());
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private (SessionSweeper Sweeper, RevocationService Revocation, ChatDbContext Db) Build(bool revocationConfigured)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OidcMetadataProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _oidc);
        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var secrets = new ConfigurationSecretResolver(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Secrets:biatec-chat:oidc-client-secret"] = "s3cret" }).Build());
        var db = NewContext();
        var revocation = new RevocationService(db, _keys, http, secrets,
            Options.Create(new RevocationOptions { Url = revocationConfigured ? RevocationUrl : null }),
            Options.Create(new OidcOptions { Issuer = "https://oidc.example.test", McpResource = "https://mcp.example.test" }),
            _time, NullLogger<RevocationService>.Instance);
        var sweeper = new SessionSweeper(db, _vault, revocation, Options.Create(new SweeperOptions()), _time, NullLogger<SessionSweeper>.Instance);
        return (sweeper, revocation, db);
    }

    private async Task<Guid> AddSessionAsync(TimeSpan idleFor, TimeSpan expiresIn, string? refreshToken = "REFRESH-1", bool openConsent = false)
    {
        var now = _time.Now;
        var conv = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using var db = NewContext();
        db.Conversations.Add(new Conversation { Id = conv, TenantId = "biatec-chat", CreatedAt = now, LastMessageAt = now });
        db.ChatSessions.Add(new ChatSession
        {
            Id = id, ConversationId = conv, Tier = Core.Data.SessionTier.Authenticated, TenantId = "biatec-chat",
            CreatedAt = now.AddHours(-1), LastActivityAt = now - idleFor, AbsoluteExpiresAt = now + expiresIn,
        });
        if (openConsent)
        {
            var preview = Guid.NewGuid();
            var step = Guid.NewGuid();
            db.ActionPreviews.Add(new ActionPreview { Id = preview, ConversationId = conv, Capability = "dex.swap", CanonicalJson = "{}", ExpiresAt = now.AddMinutes(5), Status = ActionPreviewStatus.Confirmed });
            db.ActionSteps.Add(new ActionStep { Id = step, PreviewId = preview, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = "h", ExpiresAt = now.AddMinutes(5), Status = StepStatus.AwaitingConsent });
            db.ConsentRequests.Add(new ConsentRequest { Id = Guid.NewGuid(), StepId = step, SubjectHash = "h", AuthReqId = "ar-" + id, ExpiresAt = now.AddMinutes(4), Status = Core.Data.ConsentStatus.Pending });
        }
        await db.SaveChangesAsync();
        if (refreshToken is not null)
        {
            _vault.Items[id.ToString("N")] = new TokenVaultEntry
            {
                AccessToken = "A", AccessExpiresAt = now.AddMinutes(10), RefreshToken = refreshToken, IdToken = "ID", AbsoluteExpiresAt = now + expiresIn,
            };
        }
        return id;
    }

    private async Task<bool> SessionExistsAsync(Guid id)
    {
        await using var db = NewContext();
        return await db.ChatSessions.AnyAsync(s => s.Id == id);
    }

    [Fact]
    public async Task Sweeper_EndsIdleSession_RevokesThenDeletesVault()
    {
        var (sweeper, _, _) = Build(revocationConfigured: true);
        var idle = await AddSessionAsync(SessionPolicy.IdleTimeout + TimeSpan.FromSeconds(1), TimeSpan.FromHours(5));
        var active = await AddSessionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromHours(5), "REFRESH-ACTIVE");

        var r = await sweeper.SweepAsync(default);

        Assert.Equal(1, r.Ended);
        Assert.False(await SessionExistsAsync(idle));
        Assert.True(await SessionExistsAsync(active));
        Assert.DoesNotContain(idle.ToString("N"), _vault.Items.Keys);
        Assert.Contains(active.ToString("N"), _vault.Items.Keys);
        var call = Assert.Single(_oidc.Calls);
        Assert.Contains("token=REFRESH-1", call.Body);
        Assert.Contains("token_type_hint=refresh_token", call.Body);
        Assert.StartsWith("Basic ", call.Auth);
        await using var db = NewContext();
        Assert.Empty(await db.PendingRevocations.ToListAsync()); // revoked, nothing parked
    }

    [Fact]
    public async Task Sweeper_IdleBoundary_IsExactlyTheFifteenMinuteTimeout()
    {
        var (sweeper, _, _) = Build(true);
        var justInside = await AddSessionAsync(SessionPolicy.IdleTimeout - TimeSpan.FromSeconds(1), TimeSpan.FromHours(5));
        await sweeper.SweepAsync(default);
        Assert.True(await SessionExistsAsync(justInside));

        _time.Now += TimeSpan.FromSeconds(1); // idle now equals the timeout, the same point where SessionService stops serving it
        await sweeper.SweepAsync(default);
        Assert.False(await SessionExistsAsync(justInside));
    }

    [Fact]
    public async Task Sweeper_SkipsIdleSessionWithOpenConsentRequest()
    {
        var (sweeper, _, _) = Build(true);
        var waiting = await AddSessionAsync(TimeSpan.FromMinutes(40), TimeSpan.FromHours(5), openConsent: true);

        var r = await sweeper.SweepAsync(default);

        Assert.Equal(0, r.Ended);
        Assert.True(await SessionExistsAsync(waiting));
        Assert.Contains(waiting.ToString("N"), _vault.Items.Keys);
        Assert.Empty(_oidc.Calls);

        // Once the consent request is no longer open (its window passed), the idle timeout applies again.
        _time.Now += TimeSpan.FromMinutes(5);
        await sweeper.SweepAsync(default);
        Assert.False(await SessionExistsAsync(waiting));
    }

    [Fact]
    public async Task Sweeper_SkipsIdleSessionBetweenStepsOfAMultiStepAction_UntilTheStepExpires()
    {
        var (sweeper, _, _) = Build(true);
        var s = await AddSessionAsync(TimeSpan.FromMinutes(20), TimeSpan.FromHours(5));
        await using (var db = NewContext())
        {
            var conv = (await db.ChatSessions.SingleAsync(x => x.Id == s)).ConversationId;
            var preview = Guid.NewGuid();
            db.ActionPreviews.Add(new ActionPreview { Id = preview, ConversationId = conv, Capability = "dex.liquidity", CanonicalJson = "{}", ExpiresAt = _time.Now.AddMinutes(5), Status = ActionPreviewStatus.InProgress });
            db.ActionSteps.Add(new ActionStep { Id = Guid.NewGuid(), PreviewId = preview, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = "h0", ExpiresAt = _time.Now.AddMinutes(5), Status = StepStatus.Confirmed });
            db.ActionSteps.Add(new ActionStep { Id = Guid.NewGuid(), PreviewId = preview, Seq = 1, Kind = ActionStepKind.Sign, SubjectHash = "h1", ExpiresAt = _time.Now.AddMinutes(10), Status = StepStatus.Pending });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await sweeper.SweepAsync(default)).Ended);
        Assert.True(await SessionExistsAsync(s));

        _time.Now += TimeSpan.FromMinutes(10) + SessionPolicy.InFlightGrace; // step 1's window and the grace are over
        await sweeper.SweepAsync(default);
        Assert.False(await SessionExistsAsync(s));
    }

    [Fact]
    public async Task Sweeper_AbsoluteLifetimeAlwaysEndsSession_EvenWithOpenConsentAndActivity()
    {
        var (sweeper, _, _) = Build(true);
        var s = await AddSessionAsync(TimeSpan.Zero, TimeSpan.FromMinutes(10), openConsent: true);

        await sweeper.SweepAsync(default);
        Assert.True(await SessionExistsAsync(s));

        _time.Now += TimeSpan.FromMinutes(10); // absolute expiry reached; the consent request (4 min) is long past anyway
        await using (var db = NewContext())
        {
            await db.ConsentRequests.ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresAt, _time.Now.AddMinutes(4)));
            await db.ChatSessions.ExecuteUpdateAsync(u => u.SetProperty(x => x.LastActivityAt, _time.Now)); // fully active
        }
        await sweeper.SweepAsync(default);

        Assert.False(await SessionExistsAsync(s));
        Assert.Empty(_vault.Items);
        Assert.Single(_oidc.Calls);
    }

    [Fact]
    public async Task Sweeper_FailedRevocation_ParksEncryptedTokenAndStillDeletesVault()
    {
        var (sweeper, _, _) = Build(true);
        _oidc.Status = HttpStatusCode.InternalServerError;
        var s = await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5), "REFRESH-PARK-ME");

        await sweeper.SweepAsync(default);

        Assert.False(await SessionExistsAsync(s));
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        var p = Assert.Single(await db.PendingRevocations.ToListAsync());
        Assert.Equal("biatec-chat", p.TenantId);
        Assert.Equal(_time.Now.AddDays(30), p.ExpiresAt);
        Assert.DoesNotContain("REFRESH-PARK-ME", System.Text.Encoding.UTF8.GetString(p.EncryptedRefreshToken));
    }

    [Fact]
    public async Task Sweeper_WithoutRevocationUrl_ParksWithoutAnyHttpCall()
    {
        var (sweeper, _, _) = Build(revocationConfigured: false);
        await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5));

        await sweeper.SweepAsync(default);

        Assert.Empty(_oidc.Calls);
        Assert.Empty(_vault.Items);
        await using var db = NewContext();
        Assert.Single(await db.PendingRevocations.ToListAsync());
    }

    [Fact]
    public async Task Sweeper_UnreadableVault_LeavesSessionForNextRun()
    {
        var (sweeper, _, _) = Build(true);
        var s = await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5));
        _vault.FailReads = true;

        var r = await sweeper.SweepAsync(default);

        Assert.Equal((0, 1), (r.Ended, r.Skipped));
        Assert.True(await SessionExistsAsync(s));
        Assert.NotEmpty(_vault.Items);

        _vault.FailReads = false;
        await sweeper.SweepAsync(default);
        Assert.False(await SessionExistsAsync(s));
        Assert.Single(_oidc.Calls);
    }

    [Fact]
    public async Task Sweeper_AnonymousSession_IsEndedWithoutRevocation()
    {
        var (sweeper, _, _) = Build(true);
        var s = await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5), refreshToken: null);
        await sweeper.SweepAsync(default);
        Assert.False(await SessionExistsAsync(s));
        Assert.Empty(_oidc.Calls);
    }

    [Fact]
    public async Task PendingRevocation_RetryIsNoOpUntilUrlConfigured_ThenRevokesAndDeletes()
    {
        var (sweeperNoUrl, revokerNoUrl, _) = Build(false);
        await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5), "REFRESH-LATER");
        await sweeperNoUrl.SweepAsync(default);

        Assert.Equal(0, await revokerNoUrl.RetryPendingAsync(default));
        Assert.Empty(_oidc.Calls);
        await using (var db = NewContext()) Assert.Equal(0, (await db.PendingRevocations.SingleAsync()).Attempts);

        var (_, revoker, _) = Build(true);
        _oidc.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(0, await revoker.RetryPendingAsync(default));
        await using (var db = NewContext()) Assert.Equal(1, (await db.PendingRevocations.SingleAsync()).Attempts);

        _oidc.Status = HttpStatusCode.OK;
        Assert.Equal(1, await revoker.RetryPendingAsync(default));
        Assert.Contains("token=REFRESH-LATER", _oidc.Calls.Last().Body);
        await using (var db = NewContext()) Assert.Empty(await db.PendingRevocations.ToListAsync());
    }

    [Fact]
    public async Task PendingRevocation_IsPurgedAtTokenExpiry()
    {
        var (sweeper, revoker, _) = Build(false);
        await AddSessionAsync(TimeSpan.FromHours(1), TimeSpan.FromHours(5));
        await sweeper.SweepAsync(default);

        _time.Now += TimeSpan.FromDays(30);
        await revoker.RetryPendingAsync(default);

        await using var db = NewContext();
        Assert.Empty(await db.PendingRevocations.ToListAsync());
        Assert.Empty(_oidc.Calls);
    }

    [Fact]
    public async Task Revocation_UrlOutsideIssuerOrigin_IsIgnored()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(OidcMetadataProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _oidc);
        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        await using var db = NewContext();
        var r = new RevocationService(db, _keys, http, new ConfigurationSecretResolver(new ConfigurationBuilder().Build()),
            Options.Create(new RevocationOptions { Url = "https://evil.example.test/revoke" }),
            Options.Create(new OidcOptions { Issuer = "https://oidc.example.test", McpResource = "https://mcp.example.test" }),
            _time, NullLogger<RevocationService>.Instance);
        Assert.False(r.IsConfigured);
    }
}
