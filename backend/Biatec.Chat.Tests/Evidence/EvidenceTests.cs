using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Evidence;

/// <summary>Pure encoding tests: no database needed. Expected strings are written out by hand per the spec.</summary>
public class EvidenceEncodingTests
{
    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void PayloadHash_IsSha256OfJcsSaltAndPayload_WithBigIntegersAsStrings()
    {
        var salt = new string('a', 64);
        var payload = new JsonObject { ["z"] = "1", ["amount"] = "18446744073709551615", ["n"] = 5 };
        var expected = Sha("{\"payload\":{\"amount\":\"18446744073709551615\",\"n\":5,\"z\":\"1\"},\"salt\":\"" + salt + "\"}");
        Assert.Equal(expected, EvidenceHasher.PayloadHash(salt, payload));
        Assert.Matches("^[0-9a-f]{64}$", EvidenceHasher.PayloadHash(salt, payload));
    }

    [Fact]
    public void EntryHash_UsesNumericSeq_NullAnchorRef_AndSortedKeys()
    {
        var conv = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ph = new string('b', 64);
        var expected = Sha("{\"anchorRef\":null,\"conversationId\":\"11111111-2222-3333-4444-555555555555\",\"kind\":\"message\","
            + "\"payloadHash\":\"" + ph + "\",\"prevHash\":\"" + new string('0', 64) + "\",\"seq\":1}");
        Assert.Equal(expected, EvidenceHasher.EntryHash(conv, 1, EvidenceHasher.GenesisPrevHash, "message", ph, null));

        var withAnchor = Sha("{\"anchorRef\":\"abc\",\"conversationId\":\"11111111-2222-3333-4444-555555555555\",\"kind\":\"message\","
            + "\"payloadHash\":\"" + ph + "\",\"prevHash\":\"" + new string('0', 64) + "\",\"seq\":1}");
        Assert.Equal(withAnchor, EvidenceHasher.EntryHash(conv, 1, EvidenceHasher.GenesisPrevHash, "message", ph, "abc"));
    }

    [Fact]
    public void SigningInput_IsJcsOfEntryHashKidSignedAt_Rfc3339Utc()
    {
        var t = new DateTimeOffset(2026, 10, 19, 12, 30, 5, 123, TimeSpan.FromHours(2));
        var text = Encoding.UTF8.GetString(EvidenceHasher.SigningInput("h", "k1", t));
        Assert.Equal("{\"entryHash\":\"h\",\"kid\":\"k1\",\"signedAt\":\"2026-10-19T10:30:05.123Z\"}", text);
    }

    [Fact]
    public void Genesis_Is64Zeros_AndSaltIs32BytesLowerHex()
    {
        Assert.Equal(64, EvidenceHasher.GenesisPrevHash.Length);
        Assert.All(EvidenceHasher.GenesisPrevHash, c => Assert.Equal('0', c));
        var s1 = EvidenceHasher.NewSalt();
        Assert.Matches("^[0-9a-f]{64}$", s1);
        Assert.NotEqual(s1, EvidenceHasher.NewSalt());
    }

    [Fact]
    public async Task DevKeyStore_RotationKeepsRetiredKeyPublished_AndStillVerifies()
    {
        var store = new DevEvidenceKeyStore();
        var first = await store.GetActiveKidAsync(default);
        var data = "x"u8.ToArray();
        var sig = await store.SignAsync(first, data, default);
        var second = await store.RotateAsync(default);
        Assert.NotEqual(first, second);
        Assert.Equal(second, await store.GetActiveKidAsync(default));

        var keys = await store.GetPublicKeysAsync(default);
        Assert.Equal(2, keys.Count);
        Assert.NotNull(keys.Single(k => k.Kid == first).RetiredAt);
        Assert.Null(keys.Single(k => k.Kid == second).RetiredAt);
        var k1 = keys.Single(k => k.Kid == first);
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = k1.X, Y = k1.Y } });
        Assert.True(ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task DevKeyStore_PersistsToFile_WhenConfigured()
    {
        var path = Path.Combine(Path.GetTempPath(), "evkeys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var a = new DevEvidenceKeyStore(path);
            await a.RotateAsync(default);
            var b = new DevEvidenceKeyStore(path);
            Assert.Equal(await a.GetActiveKidAsync(default), await b.GetActiveKidAsync(default));
            Assert.Equal(2, (await b.GetPublicKeysAsync(default)).Count);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Needs the docker-compose postgres. Each test class instance migrates a fresh, uniquely named database.</summary>
public sealed class EvidenceLogTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly DevEvidenceKeyStore _keys = new();

    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = true, MaxPoolSize = 40 }.ConnectionString;

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private EvidenceLog NewLog(ChatDbContext db) => new(db, _keys);

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

    private static JsonObject Payload(int i) => new() { ["text"] = $"message {i}", ["amount"] = "9007199254740993", ["i"] = i };

    private async Task<Dictionary<string, EvidencePublicKey>> KeyMap() =>
        (await _keys.GetPublicKeysAsync(default)).ToDictionary(k => k.Kid);

    [Fact]
    public async Task Chain_Verifies_After100Appends_AndFollowsTheEncodings()
    {
        var conv = Guid.NewGuid();
        await using (var db = NewContext())
        {
            var log = NewLog(db);
            for (var i = 1; i <= 100; i++) await log.AppendAsync(conv, i == 100 ? "end" : "message", Payload(i));
        }

        await using var read = NewContext();
        var chain = await NewLog(read).GetChainAsync(conv);
        Assert.Equal(100, chain.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), chain.Select(e => e.Seq));
        Assert.Equal(EvidenceHasher.GenesisPrevHash, chain[0].PrevHash);
        Assert.Equal(chain[0].EntryHash, chain[1].PrevHash);
        Assert.All(chain, e =>
        {
            Assert.Matches("^[0-9a-f]{64}$", e.EntryHash);
            Assert.Matches("^[0-9a-f]{64}$", e.PayloadHash);
            Assert.Matches("^[0-9a-f]{64}$", e.Salt!);
        });
        Assert.True((await NewLog(read).VerifyAsync(conv)).Valid);
        // The big integer stayed a decimal string in the stored payload.
        Assert.Contains("\"9007199254740993\"", chain[0].Payload);
    }

    [Fact]
    public async Task RawIntegerAbove2Pow53_IsRejected_AndNothingIsWritten()
    {
        var conv = Guid.NewGuid();
        await using var db = NewContext();
        var bad = JsonNode.Parse("{\"amount\":9007199254740993}")!.AsObject();
        await Assert.ThrowsAsync<CanonicalJsonException>(() => NewLog(db).AppendAsync(conv, "tx", bad));
        Assert.Empty(await NewLog(db).GetChainAsync(conv));
    }

    [Fact]
    public async Task DeletingPayloads_KeepsTheChainValid_AndHashesStay()
    {
        var conv = Guid.NewGuid();
        await using var db = NewContext();
        var log = NewLog(db);
        for (var i = 1; i <= 5; i++) await log.AppendAsync(conv, "message", Payload(i));
        var before = (await log.GetChainAsync(conv))[2];

        Assert.True(await log.DeletePayloadAsync(conv, 3));
        var after = (await log.GetChainAsync(conv))[2];
        Assert.Null(after.Payload);
        Assert.Null(after.Salt);
        Assert.Equal(before.PayloadHash, after.PayloadHash);
        Assert.Equal(before.EntryHash, after.EntryHash);
        Assert.True((await log.VerifyAsync(conv)).Valid);

        Assert.Equal(5, await log.DeleteAllPayloadsAsync(conv));
        Assert.True((await log.VerifyAsync(conv)).Valid);
    }

    [Fact]
    public async Task Tampering_IsDetected()
    {
        var conv = Guid.NewGuid();
        await using var db = NewContext();
        var log = NewLog(db);
        for (var i = 1; i <= 4; i++) await log.AppendAsync(conv, "message", Payload(i));
        var keys = await KeyMap();

        EvidenceEntry[] Fresh() => [.. log.GetChainAsync(conv).Result.Select(e => Clone(e))];

        var payloadEdited = Fresh();
        payloadEdited[1].Payload = "{\"text\":\"forged\"}";
        Assert.False(EvidenceVerifier.Verify(payloadEdited, keys).Valid);

        var kindEdited = Fresh();
        kindEdited[1].Kind = "other";
        Assert.False(EvidenceVerifier.Verify(kindEdited, keys).Valid);

        var removed = Fresh().Where(e => e.Seq != 2);
        Assert.False(EvidenceVerifier.Verify(removed, keys).Valid);

        var signedAtEdited = Fresh();
        signedAtEdited[2].SignedAt = signedAtEdited[2].SignedAt.AddSeconds(1);
        Assert.False(EvidenceVerifier.Verify(signedAtEdited, keys).Valid);

        var kidEdited = Fresh();
        kidEdited[0].Kid = "ev-other";
        Assert.False(EvidenceVerifier.Verify(kidEdited, keys).Valid);

        var transplanted = Fresh();
        transplanted[3].ConversationId = Guid.NewGuid();
        Assert.False(EvidenceVerifier.Verify(transplanted, keys).Valid);

        Assert.True(EvidenceVerifier.Verify(Fresh(), keys).Valid);
    }

    [Fact]
    public async Task AnchorRef_IsPartOfTheEntryHash()
    {
        var conv = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        await using var db = NewContext();
        var log = NewLog(db);
        await log.AppendAsync(conv, "start", Payload(1), anchor);
        await log.AppendAsync(conv, "message", Payload(2));
        var keys = await KeyMap();
        var chain = (await log.GetChainAsync(conv)).Select(Clone).ToArray();
        Assert.True(EvidenceVerifier.Verify(chain, keys).Valid);
        chain[0].AnchorRef = null;
        Assert.False(EvidenceVerifier.Verify(chain, keys).Valid);
    }

    [Fact]
    public async Task ConcurrentAppends_ToOneConversation_GiveAConsecutiveValidChain()
    {
        var conv = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(1, 40).Select(async i =>
        {
            await using var db = NewContext();
            await NewLog(db).AppendAsync(conv, "message", Payload(i));
        }));

        await using var read = NewContext();
        var chain = await NewLog(read).GetChainAsync(conv);
        Assert.Equal(40, chain.Count);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), chain.Select(e => e.Seq));
        Assert.True((await NewLog(read).VerifyAsync(conv)).Valid);
    }

    [Fact]
    public async Task Conversations_HaveIndependentChains()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var db = NewContext();
        var log = NewLog(db);
        await log.AppendAsync(a, "message", Payload(1));
        await log.AppendAsync(b, "message", Payload(1));
        Assert.Equal(EvidenceHasher.GenesisPrevHash, (await log.GetChainAsync(b))[0].PrevHash);
        Assert.Equal(1, (await log.GetChainAsync(b))[0].Seq);
    }

    [Fact]
    public async Task Rotation_OldEntriesStillVerify_AgainstJwks()
    {
        var conv = Guid.NewGuid();
        await using var db = NewContext();
        var log = NewLog(db);
        await log.AppendAsync(conv, "message", Payload(1));
        await _keys.RotateAsync(default);
        await log.AppendAsync(conv, "message", Payload(2));

        var chain = await log.GetChainAsync(conv);
        Assert.NotEqual(chain[0].Kid, chain[1].Kid);

        // Verify using only what the JWKS endpoint would publish.
        var jwks = EvidenceJwks.ToJwks(await _keys.GetPublicKeysAsync(default));
        var map = jwks["keys"]!.AsArray().Select(j => new EvidencePublicKey(
            (string)j!["kid"]!, EvidenceVerifier.FromBase64Url((string)j["x"]!), EvidenceVerifier.FromBase64Url((string)j["y"]!),
            DateTimeOffset.UtcNow, null)).ToDictionary(k => k.Kid);
        Assert.True(EvidenceVerifier.Verify(chain, map).Valid);
        Assert.NotNull(jwks["keys"]!.AsArray().Single(j => (string)j!["kid"]! == chain[0].Kid)!["retired_at"]);
    }

    [Fact]
    public async Task JwksEndpoint_PublishesActiveAndRetiredKeys_AndSignatureVerifiesWithIt()
    {
        var keys = new DevEvidenceKeyStore();
        var retired = await keys.GetActiveKidAsync(default);
        await keys.RotateAsync(default);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new SingleHostStore());
                s.RemoveAll<IEvidenceKeyStore>();
                s.AddSingleton<IEvidenceKeyStore>(keys);
            });
        });

        // Append through the DI-registered log, then verify the signature using only the HTTP JWKS.
        var conv = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().AppendAsync(conv, "message", Payload(1));

        using var client = factory.CreateClient();
        var res = await client.GetAsync("http://chat.biatec.io" + EvidenceJwks.Path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/jwk-set+json", res.Content.Headers.ContentType?.MediaType);
        var jwks = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["keys"]!.AsArray();
        Assert.Equal(2, jwks.Count);
        Assert.All(jwks, j => { Assert.Equal("EC", (string)j!["kty"]!); Assert.Equal("P-256", (string)j["crv"]!); Assert.Equal("ES256", (string)j["alg"]!); });
        Assert.NotNull(jwks.Single(j => (string)j!["kid"]! == retired)!["retired_at"]);

        var map = jwks.ToDictionary(j => (string)j!["kid"]!, j => new EvidencePublicKey(
            (string)j!["kid"]!, EvidenceVerifier.FromBase64Url((string)j["x"]!), EvidenceVerifier.FromBase64Url((string)j["y"]!), DateTimeOffset.UtcNow, null));
        await using var db = NewContext();
        Assert.True(EvidenceVerifier.Verify(await NewLog(db).GetChainAsync(conv), map).Valid);
    }

    private sealed class SingleHostStore : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult<Tenant?>(host == "chat.biatec.io" ? TenantSeed.BiatecChat() : null);
    }

    private static EvidenceEntry Clone(EvidenceEntry e) => new()
    {
        Id = e.Id, ConversationId = e.ConversationId, Seq = e.Seq, Kind = e.Kind, AnchorRef = e.AnchorRef, Payload = e.Payload,
        Salt = e.Salt, PayloadHash = e.PayloadHash, PrevHash = e.PrevHash, EntryHash = e.EntryHash, Kid = e.Kid,
        SignedAt = e.SignedAt, Signature = e.Signature,
    };
}
