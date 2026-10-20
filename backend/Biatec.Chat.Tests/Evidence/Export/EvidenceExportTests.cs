using System.Net;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Export;
using Biatec.Chat.Api.Evidence.Jwks;
using Biatec.Chat.Api.Sessions;
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

namespace Biatec.Chat.Tests.Evidence.Export;

/// <summary>
/// Needs the docker-compose postgres. Exercises GET /session/evidence end to end and checks the bundle with the same
/// rules the offline verifier applies. When EVIDENCE_FIXTURE_DIR is set, the bundles and the JWKS are written there so
/// packages/evidence-verifier can be tested against what the real backend emits.
/// </summary>
public sealed class EvidenceExportTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = true, MaxPoolSize = 20 }.ConnectionString;

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
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private sealed class SingleHostStore : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult<Tenant?>(host == "chat.biatec.io" ? TenantSeed.BiatecChat() : null);
    }

    private WebApplicationFactory<Program> NewFactory(DevEvidenceKeyStore keys, string? publicBaseUrl = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            ["Evidence:PublicBaseUrl"] = publicBaseUrl,
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new SingleHostStore());
            s.RemoveAll<IEvidenceKeyStore>();
            s.AddSingleton<IEvidenceKeyStore>(keys);
        });
    });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "http://chat.biatec.io" + path);
        if (token is not null) req.Headers.Add("Cookie", $"{SessionPolicy.CookieName}={token}");
        return await client.SendAsync(req);
    }

    private static Dictionary<string, EvidencePublicKey> KeyMap(JsonNode jwks) => jwks["keys"]!.AsArray().ToDictionary(j => (string)j!["kid"]!, j => new EvidencePublicKey(
        (string)j!["kid"]!, EvidenceVerifier.FromBase64Url((string)j["x"]!), EvidenceVerifier.FromBase64Url((string)j["y"]!),
        DateTimeOffset.Parse((string)j["created_at"]!), j["retired_at"] is null ? null : DateTimeOffset.Parse((string)j["retired_at"]!)));

    private static void Verify(JsonNode bundle, JsonNode jwks)
    {
        var map = KeyMap(jwks);
        var conv = Guid.Parse((string)bundle["conversationId"]!);
        var entries = bundle["entries"]!.AsArray().Select(e => new EvidenceEntry
        {
            ConversationId = conv,
            Seq = (long)e!["seq"]!,
            Kind = (string)e["kind"]!,
            AnchorRef = e["anchorRef"] is null ? null : (string)e["anchorRef"]!,
            Payload = e["payload"] is null ? null : JsonCanonicalizer.Canonicalize(e["payload"]),
            Salt = (string?)e["salt"],
            PayloadHash = (string)e["payloadHash"]!,
            PrevHash = (string)e["prevHash"]!,
            EntryHash = (string)e["entryHash"]!,
            Kid = (string)e["kid"]!,
            SignedAt = DateTimeOffset.Parse((string)e["signedAt"]!),
            Signature = (string)e["signature"]!,
        }).ToList();
        var v = EvidenceVerifier.Verify(entries, map);
        Assert.True(v.Valid, v.Error);

        // The bundle signature covers the JCS of the bundle without its signature member.
        var copy = (JsonObject)bundle.DeepClone();
        var sig = (string)copy["signature"]!;
        copy.Remove("signature");
        var k = map[(string)bundle["kid"]!];
        using var ec = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
        {
            Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            Q = new System.Security.Cryptography.ECPoint { X = k.X, Y = k.Y },
        });
        Assert.True(ec.VerifyData(JsonCanonicalizer.CanonicalizeToUtf8(copy), EvidenceVerifier.FromBase64Url(sig),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task Export_JwksUrl_UsesTheConfiguredPublicBaseUrl()
    {
        var keys = new DevEvidenceKeyStore();
        await using var factory = NewFactory(keys, "https://public.example.test/");
        var tenant = TenantSeed.BiatecChat();
        var token = SessionService.NewToken();
        var conv = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            db.Conversations.Add(new Conversation { Id = conv, TenantId = tenant.Id, CreatedAt = now, LastMessageAt = now });
            db.ChatSessions.Add(new ChatSession
            {
                Id = SessionService.IdFromToken(token), ConversationId = conv, Tier = Core.Data.SessionTier.Anonymous, TenantId = tenant.Id,
                CreatedAt = now, LastActivityAt = now, AbsoluteExpiresAt = now.AddHours(1),
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().AppendAsync(conv, "message", new JsonObject { ["text"] = "hi" });
        }
        using var client = factory.CreateClient();
        var res = await GetAsync(client, EvidenceExportEndpoints.Path, token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bundle = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("https://public.example.test" + EvidenceJwks.JwksPath, (string)bundle["jwksUrl"]!);
    }

    [Fact]
    public async Task Export_ReturnsOnlyTheSessionsOwnConversation_VerifiesAgainstJwks_AndSurvivesPayloadDeletion()
    {
        var keys = new DevEvidenceKeyStore();
        await using var factory = NewFactory(keys);
        var tenant = TenantSeed.BiatecChat();

        var token = SessionService.NewToken();
        var conv = Guid.NewGuid();
        var otherConv = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            db.Conversations.Add(new Conversation { Id = conv, TenantId = tenant.Id, CreatedAt = now, LastMessageAt = now });
            db.ChatSessions.Add(new ChatSession
            {
                Id = SessionService.IdFromToken(token), ConversationId = conv, Tier = Core.Data.SessionTier.Anonymous, TenantId = tenant.Id,
                CreatedAt = now, LastActivityAt = now, AbsoluteExpiresAt = now.AddHours(1),
            });
            await db.SaveChangesAsync();

            var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
            await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "hello", ["amount"] = "18446744073709551615" });
            await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "world", ["n"] = 2 });
            await keys.RotateAsync(default);
            await log.AppendAsync(conv, "end", new JsonObject { ["reason"] = "test" });
            await log.AppendAsync(otherConv, "message", new JsonObject { ["text"] = "someone else" });
        }

        using var client = factory.CreateClient();

        // No cookie, or a cookie that matches no live session: no bundle, and no session is created.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, EvidenceExportEndpoints.Path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, EvidenceExportEndpoints.Path, SessionService.NewToken())).StatusCode);

        var res = await GetAsync(client, EvidenceExportEndpoints.Path, token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition?.ToString());
        var bundle = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var jwks = JsonNode.Parse(await (await GetAsync(client, EvidenceJwks.JwksPath, null)).Content.ReadAsStringAsync())!;

        Assert.Equal(EvidenceBundleBuilder.Format, (string)bundle["format"]!);
        Assert.Equal(conv.ToString("D"), (string)bundle["conversationId"]!);
        Assert.Equal(3, bundle["entries"]!.AsArray().Count);
        Assert.Equal("http://chat.biatec.io" + EvidenceJwks.JwksPath, (string)bundle["jwksUrl"]!);
        Assert.Equal(2, bundle["keys"]!.AsArray().Count);
        Assert.DoesNotContain("someone else", bundle.ToJsonString());
        Verify(bundle, jwks);

        // Delete every payload: the export still verifies, from the stored hashes.
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().DeleteAllPayloadsAsync(conv);
        var deleted = JsonNode.Parse(await (await GetAsync(client, EvidenceExportEndpoints.Path, token)).Content.ReadAsStringAsync())!;
        Assert.All(deleted["entries"]!.AsArray(), e => { Assert.Null(e!["payload"]); Assert.Null(e["salt"]); });
        Verify(deleted, jwks);

        if (Environment.GetEnvironmentVariable("EVIDENCE_FIXTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "bundle.json"), bundle.ToJsonString(new() { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(dir, "bundle-deleted.json"), deleted.ToJsonString(new() { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(dir, "jwks.json"), jwks.ToJsonString(new() { WriteIndented = true }));
        }
    }
}
