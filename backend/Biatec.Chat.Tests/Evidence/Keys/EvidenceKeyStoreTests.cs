using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Evidence.Keys;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Evidence.Keys;

public class EvidenceKeyMaterialTests
{
    private static readonly byte[] Master = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task SecretMaterial_DerivedPublicPointMatchesPrivateKey_AndIsDeterministicAcrossInstances()
    {
        using var a = new SecretEvidenceKeyMaterial(Master);
        using var b = new SecretEvidenceKeyMaterial(Master);
        for (var i = 0; i < 5; i++)
        {
            var k = await a.CreateAsync(new HashSet<string>(), default);
            var data = RandomNumberGenerator.GetBytes(40);
            // A second instance with the same master signs with the same kid; the published point verifies it.
            var sig = await b.SignAsync(k.Kid, data, default);
            Assert.Equal(64, sig.Length);
            using var pub = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = k.X, Y = k.Y } });
            Assert.True(pub.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public async Task SecretMaterial_DifferentMasterCannotProduceValidSignature()
    {
        using var a = new SecretEvidenceKeyMaterial(Master);
        using var other = new SecretEvidenceKeyMaterial(RandomNumberGenerator.GetBytes(32));
        var k = await a.CreateAsync(new HashSet<string>(), default);
        var msg = new byte[] { 1, 2, 3 };
        var sig = await other.SignAsync(k.Kid, msg, default);
        using var pub = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = k.X, Y = k.Y } });
        Assert.False(pub.VerifyData(msg, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void SecretMaterial_RejectsMissingOrShortMaster()
    {
        Assert.Throws<InvalidOperationException>(() => SecretEvidenceKeyMaterial.FromBase64(null));
        Assert.Throws<InvalidOperationException>(() => SecretEvidenceKeyMaterial.FromBase64("not base64!"));
        Assert.Throws<InvalidOperationException>(() => SecretEvidenceKeyMaterial.FromBase64(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void KmsDerSignature_ConvertsToFixedWidthP1363()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = new byte[] { 9, 9, 9 };
        for (var i = 0; i < 20; i++)
        {
            var p1363 = ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var der = ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            Assert.True(ec.VerifyData(data, KmsEvidenceKeyMaterial.DerToP1363(der), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.Equal(p1363.Length, KmsEvidenceKeyMaterial.DerToP1363(der).Length);
        }
    }
}

public sealed class EvidenceKeyStoreTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly string _master = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = true, MaxPoolSize = 40 }.ConnectionString;
    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

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
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private WebApplicationFactory<Program> Host(string? env = "Testing", Dictionary<string, string?>? extra = null, string? master = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(env ?? "Testing");
            b.ConfigureAppConfiguration((_, c) =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Oidc:Issuer"] = "https://oidc.example.test",
                    ["Oidc:McpResource"] = "https://mcp.example.test",
                    ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                    ["Redis:ConnectionString"] = "127.0.0.1:1",
                    ["Postgres:ConnectionString"] = Cs,
                    ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                    ["Kms:KeyId"] = "alias/test",
                    ["Evidence:KeyStore"] = "Secret",
                    ["Evidence:Secret:MasterKey"] = master ?? _master,
                });
                if (extra is not null) c.AddInMemoryCollection(extra);
            });
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new SingleHostStore());
            });
        });

    private static async Task<Dictionary<string, EvidencePublicKey>> FetchJwks(WebApplicationFactory<Program> host)
    {
        using var client = host.CreateClient();
        var res = await client.GetAsync("http://chat.biatec.io" + EvidenceJwks.JwksPath);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var keys = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["keys"]!.AsArray();
        return keys.ToDictionary(j => (string)j!["kid"]!, j => new EvidencePublicKey(
            (string)j!["kid"]!, EvidenceVerifier.FromBase64Url((string)j["x"]!), EvidenceVerifier.FromBase64Url((string)j["y"]!),
            DateTimeOffset.Parse((string)j["created_at"]!), j["retired_at"] is null ? null : DateTimeOffset.Parse((string)j["retired_at"]!)));
    }

    private static async Task AppendAsync(WebApplicationFactory<Program> host, Guid conv, int n)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
        for (var i = 0; i < n; i++)
            await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "m" + i });
    }

    private static async Task<IReadOnlyList<EvidenceEntry>> ChainAsync(WebApplicationFactory<Program> host, Guid conv)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().GetChainAsync(conv);
    }

    [Fact]
    public async Task TwoInstancesSharingDatabaseAndSecret_ServeIdenticalJwks_AndVerifyEachOthersEntries()
    {
        await using var a = Host();
        await using var b = Host();
        var conv = Guid.NewGuid();
        await AppendAsync(a, conv, 2);
        await AppendAsync(b, conv, 2);

        var ja = await FetchJwks(a);
        var jb = await FetchJwks(b);
        Assert.Single(ja);
        Assert.Equal(ja.Keys.OrderBy(k => k), jb.Keys.OrderBy(k => k));
        Assert.All(ja, p => Assert.Equal(p.Value.X, jb[p.Key].X));

        var chain = await ChainAsync(a, conv);
        Assert.Equal(4, chain.Count);
        Assert.True(EvidenceVerifier.Verify(chain, ja).Valid);
        Assert.True(EvidenceVerifier.Verify(chain, jb).Valid);
    }

    [Fact]
    public async Task Restart_KeepsEveryKid_RetiredOnesWithDate_AndOldEntriesStillVerify()
    {
        var conv = Guid.NewGuid();
        string firstKid;
        await using (var first = Host())
        {
            await AppendAsync(first, conv, 1);
            firstKid = (await FetchJwks(first)).Keys.Single();
            await first.Services.GetRequiredService<IEvidenceKeyStore>().RotateAsync(default);
            await AppendAsync(first, conv, 1);
            Assert.Equal(2, (await FetchJwks(first)).Count);
        }

        await using var restarted = Host();
        var jwks = await FetchJwks(restarted);
        Assert.Equal(2, jwks.Count);
        Assert.NotNull(jwks[firstKid].RetiredAt);
        Assert.Single(jwks.Values, k => k.RetiredAt is null);
        await AppendAsync(restarted, conv, 1);
        var chain = await ChainAsync(restarted, conv);
        Assert.Equal(3, chain.Count);
        Assert.Equal(2, chain.Select(e => e.Kid).Distinct().Count());
        Assert.True(EvidenceVerifier.Verify(chain, jwks).Valid);
    }

    [Fact]
    public async Task RotationOnOneInstance_IsVisibleOnTheOther()
    {
        await using var a = Host();
        await using var b = Host();
        await FetchJwks(a);
        var newKid = await a.Services.GetRequiredService<IEvidenceKeyStore>().RotateAsync(default);
        Assert.Equal(newKid, await b.Services.GetRequiredService<IEvidenceKeyStore>().GetActiveKidAsync(default));
        Assert.Equal(2, (await FetchJwks(b)).Count);
    }

    [Fact]
    public async Task Database_HoldsOnlyPublicKeyMaterial()
    {
        await using var a = Host();
        await FetchJwks(a);
        await using var db = NewContext();
        var row = await db.EvidenceSigningKeys.AsNoTracking().SingleAsync();
        Assert.Equal(32, row.X.Length);
        Assert.Equal(32, row.Y.Length);
        // The table has no column that could hold a private scalar or a wrapped key.
        var columns = db.Model.FindEntityType(typeof(EvidenceSigningKey))!.GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["CreatedAt", "IsActive", "Kid", "RetiredAt", "X", "Y"], columns);
    }

    [Fact]
    public async Task StartupOutsideDevelopment_FailsWithoutConfiguredStore()
    {
        await using var host = Host("Production", new() { ["Evidence:KeyStore"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<IEvidenceKeyStore>());
        Assert.Contains("Evidence:KeyStore", ex.ToString());
    }

    [Fact]
    public async Task SecretStore_WithoutMasterKey_FailsToStart()
    {
        await using var host = Host("Production", new() { ["Evidence:Secret:MasterKey"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<IEvidenceKeyStore>());
        Assert.Contains("MasterKey", ex.ToString());
    }

    private sealed class SingleHostStore : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult<Tenant?>(host == "chat.biatec.io" ? TenantSeed.BiatecChat() : null);
    }
}
