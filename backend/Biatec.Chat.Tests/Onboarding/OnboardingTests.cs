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
    private const string ChallengeKey = "test-challenge-key";
    private const string OperatorKey = "test-operator-key";
    private const string Secret = "super-secret-client-secret-value";

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class FakeDns : IDnsTxtResolver
    {
        public readonly Dictionary<string, List<string>> Records = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Records.TryGetValue(name, out var v) ? v : []);
        public void Prove(string proofHost, string configHost, params string[] origins) =>
            Records[Challenge.RecordName(proofHost)] = [Challenge.Value(ChallengeKey, proofHost, Challenge.Digest(configHost, origins.FirstOrDefault(), origins, [configHost]))];
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
        Assert.Equal(Challenge.Value(ChallengeKey, "chat.example-bank.com", Challenge.Digest("chat.example-bank.com", "https://chat.example-bank.com", new[] { "https://chat.example-bank.com", "https://app.chat.example-bank.com", "https://www.example-bank.com" }, ["chat.example-bank.com"])), first.GetProperty("recordValue").GetString());
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
    public async Task TxtForAnotherHost_DoesNotVerify()
    {
        _registrar.Available = true;
        _dns.Records[Challenge.RecordName("chat.example-bank.com")] = [Challenge.Value(ChallengeKey, "other.example-bank.com", "x")];
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
    public async Task OperatorApproval_StillRequiresTxtProof()
    {
        var r = await PostAsync("/onboarding/operator/tenants",
            new { request = Req("chat.example-bank.com"), oidcClientId = "c", clientSecretRef = "secret://a/b" }, OperatorKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Null(await TenantAsync("chat.example-bank.com"));
    }
}
