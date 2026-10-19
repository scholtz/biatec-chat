using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Onboarding;

public sealed class OnboardingRegistrableDomainTests
{
    [Theory]
    [InlineData("chat.example-bank.com", "example-bank.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("example.com", "example.com")]
    [InlineData("foo.github.io", "foo.github.io")]
    public void Of(string host, string expected) => Assert.Equal(expected, RegistrableDomain.Of(host));

    [Fact]
    public void SameSite_DifferentDomains_AreNot() => Assert.False(RegistrableDomain.SameSite("chat.a.com", "web.b.com"));
}

/// <summary>Needs the docker-compose postgres. DNS, the OIDC registrar and the secret store are fakes.</summary>
public sealed class OnboardingTests : IAsyncLifetime
{
    private const string ChallengeKey = "test-challenge-key-0123456789-abcdefghij";
    private const string OperatorKey = "test-operator-key-0123456789-abcdefghij";
    private const string Secret = "super-secret-client-secret-value";
    private static long Bucket => Challenge.CurrentBucket(TimeProvider.System);

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class FakeDns : IDnsTxtResolver
    {
        public readonly Dictionary<string, List<string>> Records = new(StringComparer.OrdinalIgnoreCase);
        public bool Fail;
        public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct) =>
            Fail ? throw new DnsLookupException("simulated resolver outage") : Task.FromResult<IReadOnlyList<string>>(Records.TryGetValue(name, out var v) ? v : []);
        public void Prove(string proofHost, string configHost, params string[] origins) =>
            Records[Challenge.RecordName(proofHost)] = [Challenge.Value(ChallengeKey, proofHost, Challenge.Digest(configHost, origins.FirstOrDefault(), origins.Length == 0 ? ["https://" + configHost] : origins, [configHost]), Bucket)];
    }

    private sealed class FakeRegistrar : IOidcClientRegistrar
    {
        public bool Available;
        public int Calls;
        public Task<RegisteredClient?> RegisterAsync(string clientName, string redirectUri, CancellationToken ct) =>
            Task.FromResult(Available && Interlocked.Increment(ref Calls) > 0 ? new RegisteredClient("client-" + redirectUri.Split('/')[2], Secret) : null);
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Items = new();
        public Task<string> PutAsync(string path, string value, CancellationToken ct) { Items[path] = value; return Task.FromResult("secret://" + path); }
        public readonly List<string> Deleted = new();
        public Task DeleteAsync(string path, CancellationToken ct) { Deleted.Add(path); Items.Remove(path); return Task.CompletedTask; }
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeDns _dns = new();
    private readonly FakeRegistrar _registrar = new();
    private readonly FakeSecrets _secrets = new();
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
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(TenantSeed.BiatecChat());
            await db.SaveChangesAsync();
        }
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
                ["Onboarding:ChallengeKey"] = ChallengeKey,
                ["Onboarding:OperatorKey"] = OperatorKey,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<IDnsTxtResolver>();
                s.AddSingleton<IDnsTxtResolver>(_dns);
                s.RemoveAll<IOidcClientRegistrar>();
                s.AddSingleton<IOidcClientRegistrar>(_registrar);
                s.RemoveAll<ISecretStore>();
                s.AddSingleton<ISecretStore>(_secrets);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? bearer = null)
    {
        using var client = _factory.CreateClient();
        // No Origin, CSRF header or cookie: onboarding is called from tooling, not from a widget.
        var r = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io" + path) { Content = JsonContent.Create(body) };
        if (bearer is not null) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return await client.SendAsync(r);
    }

    private static object Req(string host = "chat.example-bank.com", params string[] origins) =>
        new { host, allowedOrigins = origins, signInPageUrl = origins.FirstOrDefault() };

    private async Task<Tenant?> TenantAsync(string host)
    {
        await using var db = NewContext();
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Host == host);
    }

    [Fact]
    public async Task Challenges_ListTxtRecordForHostAndForeignOriginOnly()
    {
        var r = await PostAsync("/onboarding/challenges", Req("chat.example-bank.com", "https://chat.example-bank.com", "https://app.chat.example-bank.com", "https://www.example-bank.com"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var hosts = doc.RootElement.GetProperty("challenges").EnumerateArray().Select(c => c.GetProperty("host").GetString()).ToArray();
        // The host itself and www (a sibling, never inferred); names beneath the host are covered by the host proof.
        Assert.Equal(new[] { "chat.example-bank.com", "www.example-bank.com" }, hosts.Select(h => h!).ToArray());
        var first = doc.RootElement.GetProperty("challenges")[0];
        Assert.Equal("_biatec-chat-challenge.chat.example-bank.com", first.GetProperty("recordName").GetString());
        Assert.Equal(Challenge.Value(ChallengeKey, "chat.example-bank.com", Challenge.Digest("chat.example-bank.com", "https://chat.example-bank.com", new[] { "https://chat.example-bank.com", "https://app.chat.example-bank.com", "https://www.example-bank.com" }, ["chat.example-bank.com"]), Bucket), first.GetProperty("recordValue").GetString());
    }

    [Fact]
    public async Task CrossSiteOrigin_IsRejectedWithExplanation()
    {
        var r = await PostAsync("/onboarding/challenges", Req("chat.example-bank.com", "https://evil.example"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("same-site", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    [InlineData("not a host")]
    public async Task InvalidHost_IsRejected(string host)
    {
        var r = await PostAsync("/onboarding/tenants", Req(host));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task NoTxtRecord_DoesNotBindHost()
    {
        _registrar.Available = true;
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
        Assert.Empty(_secrets.Items);
    }

    [Fact]
    public async Task ResolverFailure_IsA502NotAMissingRecordVerdict()
    {
        _registrar.Available = true;
        _dns.Fail = true;
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    [Fact]
    public async Task TxtForAnotherHost_DoesNotVerify()
    {
        _registrar.Available = true;
        _dns.Records[Challenge.RecordName("chat.example-bank.com")] = [Challenge.Value(ChallengeKey, "other.example-bank.com", "x", Bucket)];
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    [Fact]
    public async Task ForeignOriginWithoutItsOwnProof_DoesNotBind()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com", "https://chat.example-bank.com", "https://www.example-bank.com"); // the host proof does not cover the sibling www
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://chat.example-bank.com", "https://www.example-bank.com"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("www.example-bank.com", await r.Content.ReadAsStringAsync());
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    [Fact]
    public async Task Verified_RegistersClient_SecretGoesToSecretStoreNotDb()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com", "https://www.example-bank.com", "https://app.chat.example-bank.com");
        _dns.Prove("www.example-bank.com", "chat.example-bank.com", "https://www.example-bank.com", "https://app.chat.example-bank.com");
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://www.example-bank.com", "https://app.chat.example-bank.com"));
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.DoesNotContain(Secret, body);

        var t = await TenantAsync("chat.example-bank.com");
        Assert.NotNull(t);
        Assert.Equal("client-chat.example-bank.com", t.OidcClientId);
        Assert.Equal("https://chat.example-bank.com/auth/callback", t.RedirectUri);
        Assert.StartsWith("secret://", t.ClientSecretRef);
        Assert.Equal(Secret, Assert.Single(_secrets.Items).Value);
        Assert.Equal("secret://" + _secrets.Items.Keys.Single(), t.ClientSecretRef);
        Assert.False(t.TrustAnchorVerified);

        // Nothing in the stored row (every column) contains the secret value.
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(t));
        await using var conn = new NpgsqlConnection(Cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT t::text FROM \"Tenants\" t", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) Assert.DoesNotContain(Secret, rd.GetString(0));
    }

    [Fact]
    public async Task ConcurrentOnboardingOfOneHost_RegistersOnce()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        var rs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync("/onboarding/tenants", Req("chat.example-bank.com"))));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, rs.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, _registrar.Calls);
        Assert.Single(_secrets.Items);
    }

    [Theory]
    [InlineData("secret://../biatec-chat/x")]
    [InlineData("secret://a//b")]
    [InlineData("secret://biatec-chat/oidc-client-secret")] // already used by the seed tenant
    public async Task OperatorApproval_RejectsTraversalAndReusedSecretRefs(string secretRef)
    {
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        var r = await PostAsync("/onboarding/operator/tenants",
            new { request = Req("chat.example-bank.com"), oidcClientId = "c", clientSecretRef = secretRef }, OperatorKey);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    [Fact]
    public async Task ProofIsBoundToConfiguration_AttackerCannotReuseOwnersTxt()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com", "https://chat.example-bank.com");
        // Same host, different requested origins: the published TXT does not verify.
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://attacker.chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    [Fact]
    public async Task HostAlreadyBound_ToSeedTenant_Is409()
    {
        var r = await PostAsync("/onboarding/tenants", Req("chat.biatec.io"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task HostBelongsToOneTenantOnly()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        var first = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await PostAsync("/onboarding/tenants", Req("Chat.Example-Bank.com."));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var ch = await PostAsync("/onboarding/challenges", Req("chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.Conflict, ch.StatusCode);
        await using var db = NewContext();
        Assert.Equal(1, await db.Tenants.CountAsync(t => t.Host == "chat.example-bank.com"));
    }

    [Fact]
    public async Task NoDynamicRegistration_FallsBackToOperatorApproval_AndStoresNothing()
    {
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        var r = await PostAsync("/onboarding/tenants", Req("chat.example-bank.com"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("pending-operator-approval", await r.Content.ReadAsStringAsync());
        Assert.Null(await TenantAsync("chat.example-bank.com"));

        object Approval(string? clientSecretRef) => new { request = Req("chat.example-bank.com"), oidcClientId = "static-client", clientSecretRef };

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/onboarding/operator/tenants", Approval("secret://x/y"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/onboarding/operator/tenants", Approval("secret://x/y"), "wrong")).StatusCode);
        // A raw secret in place of a reference is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/onboarding/operator/tenants", Approval(Secret), OperatorKey)).StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));

        var ok = await PostAsync("/onboarding/operator/tenants", Approval("secret://tenants/chat.example-bank.com/oidc-client-secret"), OperatorKey);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var t = await TenantAsync("chat.example-bank.com");
        Assert.Equal("static-client", t!.OidcClientId);
        Assert.Equal("secret://tenants/chat.example-bank.com/oidc-client-secret", t.ClientSecretRef);
    }

    [Fact]
    public async Task OperatorApproval_AcceptsLowercaseBearerScheme_AndOnlyOwnTenantsPath()
    {
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        object Body(string r) => new { request = Req("chat.example-bank.com"), oidcClientId = "c", clientSecretRef = r };
        using var client = _factory.CreateClient();
        async Task<HttpStatusCode> Send(string r)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/onboarding/operator/tenants") { Content = JsonContent.Create(Body(r)) };
            m.Headers.TryAddWithoutValidation("Authorization", "bearer " + OperatorKey);
            return (await client.SendAsync(m)).StatusCode;
        }
        // Another host's reserved path is refused, the tenant's own path is accepted.
        Assert.Equal(HttpStatusCode.BadRequest, await Send("secret://tenants/other.example-bank.com/oidc-client-secret"));
        Assert.Equal(HttpStatusCode.Created, await Send("secret://tenants/chat.example-bank.com/oidc-client-secret"));
    }

    [Fact]
    public async Task OperatorApproval_StillRequiresTxtProof()
    {
        var r = await PostAsync("/onboarding/operator/tenants",
            new { request = Req("chat.example-bank.com"), oidcClientId = "c", clientSecretRef = "secret://a/b" }, OperatorKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }

    private HttpClient ClientWith(params (string Key, string? Value)[] config) =>
        _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(config.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))))).CreateClient();

    [Fact]
    public async Task Cors_ChallengesAnswersWildcard_OperatorRouteNever()
    {
        using var client = _factory.CreateClient();
        async Task<HttpResponseMessage> Send(string path)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io" + path) { Content = JsonContent.Create(Req("localhost")) };
            m.Headers.TryAddWithoutValidation("Origin", "https://tooling.example");
            m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + OperatorKey);
            return await client.SendAsync(m);
        }
        var open = await Send("/onboarding/challenges");
        Assert.Equal("*", Assert.Single(open.Headers.GetValues("Access-Control-Allow-Origin")));
        var op = await Send("/onboarding/operator/tenants");
        Assert.False(op.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(op.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    public async Task OperatorRoute_IsNotFound_WhenKeyUnsetOrTooShort(string? key)
    {
        using var client = ClientWith(("Onboarding:OperatorKey", key));
        var m = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/onboarding/operator/tenants") { Content = JsonContent.Create(new { }) };
        m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (key ?? ""));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(m)).StatusCode);
    }

    [Fact]
    public async Task OversizedBody_IsRejectedWith413()
    {
        using var client = _factory.CreateClient();
        var m = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/onboarding/challenges")
        {
            Content = new StringContent("{\"host\":\"" + new string('a', 64 * 1024) + "\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(m)).StatusCode);
    }

    [Fact]
    public async Task RateLimit_AppliesToOnboardingOnly()
    {
        using var client = ClientWith(("Onboarding:RateLimitPermits", "2"), ("Onboarding:RateLimitWindowSeconds", "600"));
        async Task<HttpStatusCode> Send()
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/onboarding/challenges") { Content = JsonContent.Create(Req("localhost")) };
            return (await client.SendAsync(m)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Send());
        Assert.Equal(HttpStatusCode.BadRequest, await Send());
        Assert.Equal(HttpStatusCode.TooManyRequests, await Send());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("http://chat.biatec.io/health")).StatusCode);
    }

    [Fact]
    public async Task Challenge_AcceptsCurrentAndPreviousBucket_NotOlder()
    {
        var digest = Challenge.Digest("chat.example-bank.com", "https://chat.example-bank.com", ["https://chat.example-bank.com"], ["chat.example-bank.com"]);
        var name = Challenge.RecordName("chat.example-bank.com");
        _dns.Records[name] = [Challenge.Value(ChallengeKey, "chat.example-bank.com", digest, Bucket - 1)];
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://chat.example-bank.com"))).StatusCode);
        _dns.Records[name] = [Challenge.Value(ChallengeKey, "chat.example-bank.com", digest, Bucket - 2)];
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync("/onboarding/tenants", Req("chat.example-bank.com", "https://chat.example-bank.com"))).StatusCode);
    }

    [Fact]
    public async Task FailedRowWrite_RemovesTheOrphanedSecret()
    {
        _registrar.Available = true;
        _dns.Prove("chat.example-bank.com", "chat.example-bank.com");
        await using (var conn = new NpgsqlConnection(Cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE FUNCTION fail_insert() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END $$ LANGUAGE plpgsql; " +
                "CREATE TRIGGER t_fail BEFORE INSERT ON \"Tenants\" FOR EACH ROW EXECUTE FUNCTION fail_insert();", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => PostAsync("/onboarding/tenants", Req("chat.example-bank.com")));
        Assert.Equal("tenants/chat.example-bank.com/oidc-client-secret", Assert.Single(_secrets.Deleted));
        Assert.Empty(_secrets.Items);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }
}

/// <summary>DnsClientTxtResolver against a loopback UDP DNS server that answers with a fixed response code.</summary>
public sealed class DnsClientTxtResolverTests
{
    private static async Task<IReadOnlyList<string>> QueryAsync(int rcode, string? txt)
    {
        using var server = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.UdpReceiveResult rx;
                try { rx = await server.ReceiveAsync(); } catch (Exception) { return; }
                var q = rx.Buffer;
                var end = 12;
                while (q[end] != 0) end += q[end] + 1;
                end += 5; // root label, QTYPE, QCLASS
                var resp = new List<byte>(q.Take(end));
                resp[2] = 0x81;
                resp[3] = (byte)(0x80 | rcode);
                resp[6] = 0; resp[7] = (byte)(txt is null ? 0 : 1); // ANCOUNT
                resp[10] = 0; resp[11] = 0; // ARCOUNT
                if (txt is not null)
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes(txt);
                    resp.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 0, 0, (byte)(bytes.Length + 1), (byte)bytes.Length]);
                    resp.AddRange(bytes);
                }
                await server.SendAsync(resp.ToArray(), resp.Count, rx.RemoteEndPoint);
            }
        });
        var lookup = new DnsClient.LookupClient(new DnsClient.LookupClientOptions(new IPEndPoint(IPAddress.Loopback, port))
        { UseCache = false, Timeout = TimeSpan.FromSeconds(3), Retries = 0, UseTcpFallback = false });
        try { return await new DnsClientTxtResolver(lookup).GetTxtRecordsAsync("_biatec-chat-challenge.example.test", CancellationToken.None); }
        finally { server.Close(); await serving; }
    }

    [Fact]
    public async Task NxDomain_MeansNoRecords() => Assert.Empty(await QueryAsync(rcode: 3, txt: null));

    [Fact]
    public async Task NoError_ReturnsTxt() => Assert.Equal("hello", Assert.Single(await QueryAsync(rcode: 0, txt: "hello")));

    [Fact]
    public async Task ServFail_IsALookupFailureNotAVerdict() =>
        await Assert.ThrowsAsync<DnsLookupException>(() => QueryAsync(rcode: 2, txt: null));
}
