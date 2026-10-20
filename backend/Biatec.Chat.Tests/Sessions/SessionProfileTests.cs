using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Biatec.Chat.Tests.Sessions;

/// <summary>T-083: GET /session carries the signed-in email (ID token claim) and primary address (Biatec MCP), never persisted.</summary>
public sealed class SessionProfileTests : IAsyncLifetime
{
    private const string Email = "alice@example.test";
    private const string Primary = "PRIMARYADDRESS234567";
    private const string Origin = "https://web.biatec.io";

    internal static string IdToken(string? email)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = email is null ? "{\"sub\":\"alice-sub\"}" : $"{{\"sub\":\"alice-sub\",\"email\":\"{email}\"}}";
        return $"{B64("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{B64(payload)}.c2ln";
    }

    private static TokenVaultEntry Entry(string? idToken, TimeSpan? accessLeft = null) => new()
    {
        AccessToken = "ACCESS-TOKEN-SECRET",
        AccessExpiresAt = DateTimeOffset.UtcNow + (accessLeft ?? TimeSpan.FromMinutes(5)),
        RefreshToken = "REFRESH-TOKEN-SECRET",
        IdToken = idToken,
        Scopes = ["openid"],
        AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
    };

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Exception? Throw;
        public int Reads;
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default)
        {
            Reads++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Items.GetValueOrDefault(id));
        }
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private sealed class FakeMcp : IBiatecMcpClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public McpCallContext? LastCtx;
        public CancellationToken LastToken;
        public Func<CancellationToken, Task<McpToolResult>> Addresses = _ => Task.FromResult(Ok($"[\"{Primary}\",\"SECONDADDRESS\"]"));

        public static McpToolResult Ok(string json) => new(false, json, JsonDocument.Parse(json).RootElement.Clone());

        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastCtx = ctx;
            LastToken = ct;
            return Addresses(ct);
        }

        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ---- SessionProfile unit tests ----

    private readonly MemoryVault _vault = new();
    private readonly FakeMcp _mcp = new();

    private SessionProfile NewProfile(SessionProfileOptions? o = null) =>
        new(_vault, _mcp, new MemoryCache(new MemoryCacheOptions()), Options.Create(o ?? new SessionProfileOptions()),
            Options.Create(new VaultOptions()), TimeProvider.System, NullLogger<SessionProfile>.Instance);

    private ChatSession SignedIn(string? idToken = null, TimeSpan? accessLeft = null)
    {
        var s = new ChatSession { Id = Guid.NewGuid(), Tier = SessionTier.Authenticated, TenantId = "biatec-chat", OidcSub = "alice-sub" };
        _vault.Items[s.Id.ToString("N")] = Entry(idToken ?? IdToken(Email), accessLeft);
        return s;
    }

    /// <summary>Polls like the widget until the background fill has published an address (or the deadline passes).</summary>
    private static async Task<(string? Email, string? Address)> PollAsync(SessionProfile p, ChatSession s, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var r = await p.GetAsync(s, default);
            if (r.PrimaryAddress is not null || DateTime.UtcNow > deadline) return r;
            await Task.Delay(20);
        }
    }

    private void SetScopes(string id, params string[] scopes)
    {
        var e = _vault.Items[id];
        _vault.Items[id] = new TokenVaultEntry
        {
            AccessToken = e.AccessToken, AccessExpiresAt = e.AccessExpiresAt, RefreshToken = e.RefreshToken, IdToken = e.IdToken,
            Scopes = scopes, AbsoluteExpiresAt = e.AbsoluteExpiresAt,
        };
    }

    [Fact]
    public async Task Scopes_Guest_AreEmpty()
    {
        var s = new ChatSession { Id = Guid.NewGuid(), Tier = SessionTier.Anonymous, TenantId = "biatec-chat" };
        Assert.Empty((await NewProfile().GetProfileAsync(s, default)).Scopes);
        Assert.Equal(0, _vault.Reads);
    }

    [Fact]
    public async Task Scopes_SignedIn_AreReturned_WithoutExtraVaultReadPerPoll()
    {
        var s = SignedIn();
        var id = s.Id.ToString("N");
        SetScopes(id, "openid", "sign");
        var p = NewProfile();
        for (var i = 0; i < 5; i++) Assert.Equal(["openid", "sign"], (await p.GetProfileAsync(s, default)).Scopes);
        await PollAsync(p, s); // address fill done, entry refreshed in the cache
        for (var i = 0; i < 5; i++) Assert.Equal(["openid", "sign"], (await p.GetProfileAsync(s, default)).Scopes);
        Assert.Equal(1, _vault.Reads);
    }

    [Fact]
    public async Task Invalidate_RereadsScopes()
    {
        var s = SignedIn();
        var id = s.Id.ToString("N");
        var p = NewProfile();
        Assert.Equal(["openid"], (await p.GetProfileAsync(s, default)).Scopes);
        SetScopes(id, "openid", "sign");
        Assert.Equal(["openid"], (await p.GetProfileAsync(s, default)).Scopes); // cached
        p.Invalidate(id);
        Assert.Equal(["openid", "sign"], (await p.GetProfileAsync(s, default)).Scopes);
    }

    [Fact]
    public async Task Guest_GetsNulls_WithoutVaultOrMcpCalls()
    {
        var s = new ChatSession { Id = Guid.NewGuid(), Tier = SessionTier.Anonymous, TenantId = "biatec-chat" };
        Assert.Equal((null, null), await NewProfile().GetAsync(s, default));
        Assert.Equal(0, _vault.Reads);
        Assert.Equal(0, _mcp.Calls);
    }

    [Fact]
    public async Task SignedIn_GetsEmailFromIdTokenAndFirstMcpAddress()
    {
        var s = SignedIn();
        var p = NewProfile();
        Assert.Equal((Email, null), await p.GetAsync(s, default)); // never waits for Biatec MCP
        var (email, address) = await PollAsync(p, s);
        Assert.Equal(Email, email);
        Assert.Equal(Primary, address);
        Assert.Equal(s.Id.ToString("N"), _mcp.LastCtx!.SessionId);
        Assert.Equal("biatec-chat", _mcp.LastCtx.TenantId);
        Assert.Equal("openid", _mcp.LastCtx.ScopeKey);
    }

    [Fact]
    public async Task McpCall_GetsNoCancellableToken_SoAVaultRefreshIsNeverCut()
    {
        var s = SignedIn();
        var p = NewProfile();
        using var cts = new CancellationTokenSource();
        await p.GetAsync(s, cts.Token);
        await PollAsync(p, s);
        Assert.False(_mcp.LastToken.CanBeCanceled);
    }

    [Fact]
    public async Task AccessTokenNeedingRefresh_SkipsTheMcpLookup()
    {
        var s = SignedIn(accessLeft: TimeSpan.FromSeconds(40)); // inside RefreshSkew (30 s) + TokenMargin (1 min)
        var p = NewProfile();
        Assert.Equal((Email, null), await p.GetAsync(s, default));
        await Task.Delay(100);
        Assert.Equal(0, _mcp.Calls);
    }

    [Fact]
    public async Task Profile_IsCachedPerSession_NoVaultReadOrMcpCallOnLaterPolls()
    {
        var s = SignedIn();
        var p = NewProfile();
        await PollAsync(p, s);
        var reads = _vault.Reads;
        Assert.Equal((Email, Primary), await p.GetAsync(s, default));
        Assert.Equal((Email, Primary), await p.GetAsync(s, default));
        Assert.Equal(reads, _vault.Reads);
        Assert.Equal(1, _mcp.Calls);
    }

    [Fact]
    public async Task Fill_IsSingleFlightedPerSession()
    {
        var gate = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mcp.Addresses = _ => gate.Task;
        var s = SignedIn();
        // A tiny failure TTL makes every poll a cache miss while the first call is still running.
        var p = NewProfile(new SessionProfileOptions { FailureTtl = TimeSpan.FromMilliseconds(1), McpTimeout = TimeSpan.FromSeconds(30) });
        await p.GetAsync(s, default); // starts the background fill
        // Wait for the first call to really be in flight (it is started on the thread pool, which can lag under load).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_mcp.Calls < 1 && DateTime.UtcNow < deadline) await Task.Yield();
        Assert.Equal(1, _mcp.Calls);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => { await Task.Delay(5); await p.GetAsync(s, default); }));
        Assert.Equal(1, _mcp.Calls);
        gate.SetResult(FakeMcp.Ok($"[\"{Primary}\"]"));
        Assert.Equal(Primary, (await PollAsync(p, s)).Address);
        Assert.Equal(1, _mcp.Calls);
    }

    [Fact]
    public async Task McpOutage_KeepsEmail_AddressNull_AndIsNotRetriedOnEveryPoll()
    {
        _mcp.Addresses = _ => throw new HttpRequestException("down");
        var s = SignedIn();
        var p = NewProfile();
        Assert.Equal((Email, null), await PollAsync(p, s, TimeSpan.FromMilliseconds(300)));
        Assert.Equal((Email, null), await p.GetAsync(s, default));
        Assert.Equal(1, _mcp.Calls);
    }

    [Fact]
    public async Task McpToolError_AddressNull()
    {
        _mcp.Addresses = _ => Task.FromResult(FakeMcp.Ok("{\"error\":\"no wallet\"}"));
        var s = SignedIn();
        Assert.Equal((Email, null), await PollAsync(NewProfile(), s, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, _mcp.Calls);
    }

    [Fact]
    public async Task McpHang_TimesOut_AddressNull_HoldsTheSlot()
    {
        var hang = new TaskCompletionSource<McpToolResult>();
        _mcp.Addresses = _ => hang.Task;
        var s = SignedIn();
        var p = NewProfile(new SessionProfileOptions { McpTimeout = TimeSpan.FromMilliseconds(50), FailureTtl = TimeSpan.FromMilliseconds(1) });
        Assert.Equal((Email, null), await PollAsync(p, s, TimeSpan.FromMilliseconds(300)));
        // Timed out, but the hung call still owns the slot: no second call per poll.
        Assert.Equal(1, _mcp.Calls);
        hang.SetResult(FakeMcp.Ok("[]"));
    }

    [Fact]
    public async Task VaultOutage_GivesNulls()
    {
        var s = SignedIn();
        _vault.Throw = new InvalidOperationException("redis down");
        Assert.Equal((null, null), await NewProfile().GetAsync(s, default));
        Assert.Equal(0, _mcp.Calls);
    }

    [Fact]
    public async Task NoVaultEntry_GivesNulls()
    {
        var s = new ChatSession { Id = Guid.NewGuid(), Tier = SessionTier.Authenticated, TenantId = "biatec-chat" };
        Assert.Equal((null, null), await NewProfile().GetAsync(s, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void EmailOf_MissingOrMalformedToken_IsNull(string? token) => Assert.Null(SessionProfile.EmailOf(token));

    [Fact]
    public void EmailOf_TokenWithoutEmailClaim_IsNull() => Assert.Null(SessionProfile.EmailOf(IdToken(null)));

    // ---- GET /session end to end (docker-compose postgres) ----

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private WebApplicationFactory<Program> _factory = null!;
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

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
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.RemoveAll<IBiatecMcpClient>();
                s.AddSingleton<IBiatecMcpClient>(_mcp);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null)
    {
        using var client = _factory.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Get, "http://chat.biatec.io" + path);
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return await client.SendAsync(r);
    }

    /// <summary>A session signed in the way the callback leaves it: Authenticated tier, OIDC sub, vault entry.</summary>
    private async Task<string> SignedInCookieAsync()
    {
        var r = await GetAsync("/session/probe");
        var setCookie = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        var token = setCookie[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];
        var id = SessionService.IdFromToken(token);
        await using var db = NewContext();
        await db.ChatSessions.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Tier, SessionTier.Authenticated).SetProperty(s => s.OidcSub, "alice-sub"));
        _vault.Items[id.ToString("N")] = Entry(IdToken(Email));
        return token;
    }

    [Fact]
    public async Task GetSession_SignedIn_ReturnsEmailAndPrimaryAddress_NoTokens()
    {
        var cookie = await SignedInCookieAsync();
        HttpResponseMessage r;
        string body;
        JsonElement j;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do // the first poll answers before Biatec MCP; the address arrives on a later one
        {
            r = await GetAsync("/session", cookie);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            body = await r.Content.ReadAsStringAsync();
            j = JsonDocument.Parse(body).RootElement;
            if (j.TryGetProperty("primaryAddress", out var pa) && pa.ValueKind == JsonValueKind.String) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("Authenticated", j.GetProperty("tier").GetString());
        Assert.Equal(Email, j.GetProperty("email").GetString());
        Assert.Equal(Primary, j.GetProperty("primaryAddress").GetString());
        Assert.Equal(["openid"], j.GetProperty("scopes").EnumerateArray().Select(x => x.GetString()).ToArray());
        var reads = _vault.Reads;
        for (var i = 0; i < 3; i++) await GetAsync("/session", cookie);
        Assert.Equal(reads, _vault.Reads); // polls are served from the cached profile: no vault read
        foreach (var secret in new[] { "eyJ", "ACCESS-TOKEN-SECRET", "REFRESH-TOKEN-SECRET" })
            Assert.DoesNotContain(secret, body);
    }

    [Fact]
    public async Task GetSession_Guest_ReturnsNulls()
    {
        var r = await GetAsync("/session");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anonymous", j.GetProperty("tier").GetString());
        Assert.True(!j.TryGetProperty("email", out var e) || e.ValueKind == JsonValueKind.Null);
        Assert.True(!j.TryGetProperty("primaryAddress", out var a) || a.ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Array, j.GetProperty("scopes").ValueKind);
        Assert.Equal(0, j.GetProperty("scopes").GetArrayLength());
        Assert.Equal(0, _mcp.Calls);
    }

    [Fact]
    public async Task GetSession_McpOutage_StillReturnsTheSession_AddressNull()
    {
        _mcp.Addresses = _ => throw new McpToolUnavailableException("listAlgorandAddresses");
        var cookie = await SignedInCookieAsync();
        var r = await GetAsync("/session", cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await Task.Delay(200);
        r = await GetAsync("/session", cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Email, j.GetProperty("email").GetString());
        Assert.True(!j.TryGetProperty("primaryAddress", out var a) || a.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GetSession_PersistsNeitherEmailNorAddress()
    {
        var cookie = await SignedInCookieAsync();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/session", cookie)).StatusCode);
        await Task.Delay(200);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/session", cookie)).StatusCode);
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog','information_schema') AND column_name ILIKE '%email%'", conn))
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        // The session row holds the sub only: no column of it contains the email or the address.
        await using (var cmd = new NpgsqlCommand("SELECT row_to_json(s)::text FROM \"ChatSessions\" s", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync())
            {
                Assert.DoesNotContain(Email, rd.GetString(0));
                Assert.DoesNotContain(Primary, rd.GetString(0));
            }
    }
}
