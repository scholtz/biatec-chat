using System.Net;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Retention;
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
using Microsoft.Extensions.Options;
using Npgsql;

namespace Biatec.Chat.Tests.Retention;

/// <summary>Needs the docker-compose postgres. The retention job runs against a fake clock; erasure goes through the HTTP endpoint.</summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SingleHostStore : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult<Tenant?>(host == "chat.biatec.io" ? TenantSeed.BiatecChat() : null);
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Origin = "https://web.biatec.io";
    private const string TenantId = "biatec-chat";
    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
    private readonly FakeTime _time = new();

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

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

    private RetentionService Retention(ChatDbContext db) => new(db, Options.Create(new RetentionOptions()), _time);

    private async Task<Guid> SeedConversationAsync(ChatDbContext db, string? sub, bool withPreview = false, bool withSession = false)
    {
        var now = _time.GetUtcNow();
        var id = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = id, TenantId = TenantId, OidcSub = sub, CreatedAt = now, LastMessageAt = now });
        db.Messages.Add(new Message { ConversationId = id, Seq = 1, Role = MessageRole.User, Content = "send 5 ALGO to bob", ToolCalls = "[{\"name\":\"x\"}]", ToolResults = "[{\"ok\":true}]" });
        db.PublicConversationStates.Add(new PublicConversationState { ConversationId = id, CapabilityHash = "c", ContinuationTokenHash = "t" + id });
        if (withSession)
            db.ChatSessions.Add(new ChatSession { Id = Guid.NewGuid(), ConversationId = id, TenantId = TenantId, CreatedAt = now, LastActivityAt = now, AbsoluteExpiresAt = now.AddHours(8) });
        if (withPreview)
        {
            var preview = new ActionPreview
            {
                Id = Guid.NewGuid(), ConversationId = id, Capability = "wallet.transfer", CanonicalJson = "{\"to\":\"BOB\",\"amount\":\"5\"}",
                Status = ActionPreviewStatus.Completed, ExpiresAt = now.AddMinutes(5),
            };
            var step = new ActionStep
            {
                Id = Guid.NewGuid(), PreviewId = preview.Id, Seq = 1, Kind = ActionStepKind.Sign, SubjectHash = new string('a', 64),
                UnsignedGroup = "[\"gqN0eG4\"]", SenderAddress = "SENDERADDR", LimitsRequest = "{\"daily\":1}", Network = "testnet",
                ExpiresAt = now.AddMinutes(5), Status = StepStatus.Confirmed, TxIds = ["TX1"],
            };
            db.ActionPreviews.Add(preview);
            db.ActionSteps.Add(step);
            db.ConsentRequests.Add(new ConsentRequest
            {
                Id = Guid.NewGuid(), StepId = step.Id, SubjectHash = step.SubjectHash, AuthReqId = "ar-" + id, ExpiresAt = now.AddMinutes(5),
                Status = ConsentStatus.Approved, Receipt = "{\"sub\":\"pairwise\",\"sig\":\"x\"}",
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<EvidenceVerification> VerifyAsync(IEvidenceLog log, Guid conv) => await log.VerifyAsync(conv);

    [Fact]
    public async Task Retention_RemovesPayloadsPastTheWindow_AndTheChainStillVerifies()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var conv = await SeedConversationAsync(db, "sub-1", withPreview: true);
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "old" });
        await log.AppendAsync(conv, "preview", new JsonObject { ["canonicalJson"] = "{}" });

        _time.Now += TimeSpan.FromDays(89);
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "newer" });
        await db.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastMessageAt, _time.Now));

        // Day 89: nothing is past the window yet.
        Assert.Equal(new RetentionResult(0, 0, 0, 0), await Retention(db).ApplyAsync(default));

        _time.Now += TimeSpan.FromDays(2); // the first two entries are 91 days old, the third is 2 days old
        var r = await Retention(db).ApplyAsync(default);
        Assert.Equal(2, r.PayloadsDeleted);
        var chain = await log.GetChainAsync(conv);
        Assert.All(chain.Take(2), e => { Assert.Null(e.Payload); Assert.Null(e.Salt); });
        Assert.NotNull(chain[2].Payload);
        Assert.True((await VerifyAsync(log, conv)).Valid);
        // The conversation itself had a message two days ago, so its transcript stays.
        Assert.Equal("send 5 ALGO to bob", (await db.Messages.AsNoTracking().SingleAsync(m => m.ConversationId == conv)).Content);

        _time.Now += TimeSpan.FromDays(90);
        r = await Retention(db).ApplyAsync(default);
        Assert.Equal(1, r.PayloadsDeleted);
        Assert.Equal(1, r.TranscriptsCleared);
        Assert.True((await VerifyAsync(log, conv)).Valid);
        var m = await db.Messages.AsNoTracking().SingleAsync(x => x.ConversationId == conv);
        Assert.Equal("", m.Content);
        Assert.Null(m.ToolCalls);
        Assert.Null(m.ToolResults);
        var step = await db.ActionSteps.AsNoTracking().SingleAsync();
        Assert.Null(step.UnsignedGroup);
        Assert.Null(step.SenderAddress);
        Assert.Equal(["TX1"], step.TxIds);
        Assert.Equal(new string('a', 64), step.SubjectHash);
        Assert.NotNull((await db.ConsentRequests.AsNoTracking().SingleAsync()).Receipt);
        Assert.Equal(RetentionService.BlankCanonicalJson, (await db.ActionPreviews.AsNoTracking().SingleAsync()).CanonicalJson);
        // A signed-in conversation is kept: the chain and the receipts are records.
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == conv));
        Assert.Equal(new RetentionResult(0, 0, 0, 0), await Retention(db).ApplyAsync(default));
    }

    [Fact]
    public async Task Retention_RemovesAnonymousConversationsAfter30Days_KeepsChainsReceiptsAndPendingRevocations()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var anon = await SeedConversationAsync(db, null);
        var anonWithSession = await SeedConversationAsync(db, null, withSession: true);
        var erasedFundMoving = await SeedConversationAsync(db, null, withPreview: true);
        var signedIn = await SeedConversationAsync(db, "sub-1");
        await log.AppendAsync(anon, "message", new JsonObject { ["text"] = "hello" });
        await log.AppendAsync(anon, "end", new JsonObject { ["reason"] = "idle" });
        db.PopupAuthRequests.Add(new PopupAuthRequest { Id = Guid.NewGuid(), ConversationId = anon, NonceHash = "n", ExpiresAt = _time.Now });
        var revocation = new PendingRevocation { Id = Guid.NewGuid(), TenantId = TenantId, EncryptedRefreshToken = [1, 2, 3], ExpiresAt = _time.Now.AddDays(1) };
        db.PendingRevocations.Add(revocation);
        await db.SaveChangesAsync();

        _time.Now += TimeSpan.FromDays(29);
        Assert.Equal(0, (await Retention(db).ApplyAsync(default)).AnonymousRemoved);

        _time.Now += TimeSpan.FromDays(2);
        var r = await Retention(db).ApplyAsync(default);
        Assert.Equal(1, r.AnonymousRemoved);
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == anon));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == anon));
        Assert.False(await db.PublicConversationStates.AnyAsync(p => p.ConversationId == anon));
        Assert.False(await db.PopupAuthRequests.AnyAsync(p => p.ConversationId == anon));
        // Its chain stays without payloads and still verifies.
        var chain = await log.GetChainAsync(anon);
        Assert.Equal(2, chain.Count);
        Assert.All(chain, e => Assert.Null(e.Payload));
        Assert.True((await VerifyAsync(log, anon)).Valid);
        // A session not yet swept, an erased conversation with a fund-moving action, and a signed-in one all stay.
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == anonWithSession));
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == erasedFundMoving));
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == signedIn));
        Assert.NotNull((await db.ConsentRequests.AsNoTracking().SingleAsync()).Receipt);
        Assert.True(await db.PendingRevocations.AnyAsync(p => p.Id == revocation.Id));

        // Past the record window, everything goes except what still has a session, and PendingRevocation rows
        // (even long expired: their own service purges them) are never touched here.
        _time.Now += TimeSpan.FromDays(2557);
        r = await Retention(db).ApplyAsync(default);
        Assert.Equal(3, r.RecordsRemoved); // erasedFundMoving, signedIn, and the orphaned chain of anon
        Assert.Empty(await log.GetChainAsync(anon));
        Assert.False(await db.ConsentRequests.AnyAsync());
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == signedIn));
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == anonWithSession));
        Assert.True(await db.PendingRevocations.AnyAsync(p => p.Id == revocation.Id));
    }

    // ---- Erasure through the HTTP endpoint ----

    private WebApplicationFactory<Program> NewFactory(DevEvidenceKeyStore keys) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
            ["Retention:Enabled"] = "false",
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new SingleHostStore());
            s.RemoveAll<IEvidenceKeyStore>();
            s.AddSingleton<IEvidenceKeyStore>(keys);
        });
    });

    private static HttpRequestMessage Req(HttpMethod m, string path, string? cookie, bool csrf = true)
    {
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        r.Headers.TryAddWithoutValidation("Origin", Origin);
        if (csrf)
        {
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return r;
    }

    private static Dictionary<string, EvidencePublicKey> Map(IReadOnlyList<EvidencePublicKey> keys) => keys.ToDictionary(k => k.Kid);

    private static EvidenceVerification VerifyBundle(JsonNode bundle, IReadOnlyDictionary<string, EvidencePublicKey> keys)
    {
        var conv = Guid.Parse((string)bundle["conversationId"]!);
        var entries = bundle["entries"]!.AsArray().Select(e => new EvidenceEntry
        {
            ConversationId = conv,
            Seq = (long)e!["seq"]!,
            Kind = (string)e["kind"]!,
            AnchorRef = e["anchorRef"] is null ? null : Guid.Parse((string)e["anchorRef"]!),
            Payload = e["payload"] is null ? null : JsonCanonicalizer.Canonicalize(e["payload"]),
            Salt = (string?)e["salt"],
            PayloadHash = (string)e["payloadHash"]!,
            PrevHash = (string)e["prevHash"]!,
            EntryHash = (string)e["entryHash"]!,
            Kid = (string)e["kid"]!,
            SignedAt = DateTimeOffset.Parse((string)e["signedAt"]!),
            Signature = (string)e["signature"]!,
        }).ToList();
        return EvidenceVerifier.Verify(entries, keys);
    }

    [Fact]
    public async Task Erasure_BlanksExactlyTheListedFields_KeepsHashesReceiptsTxIds_AndEveryBundleStillVerifies()
    {
        _time.Now = DateTimeOffset.UtcNow;
        using var keys = new DevEvidenceKeyStore();
        await using var factory = NewFactory(keys);
        const string sub = "pairwise-sub-1";
        var token = SessionService.NewToken();
        Guid current, older, foreign;
        PendingRevocation revocation;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            current = await SeedConversationAsync(db, sub, withPreview: true);
            older = await SeedConversationAsync(db, sub);
            foreign = await SeedConversationAsync(db, "someone-else");
            db.ChatSessions.Add(new ChatSession
            {
                Id = SessionService.IdFromToken(token), ConversationId = current, Tier = SessionTier.Authenticated, OidcSub = sub, TenantId = TenantId,
                CreatedAt = _time.Now, LastActivityAt = _time.Now, AbsoluteExpiresAt = _time.Now.AddHours(1),
            });
            revocation = new PendingRevocation { Id = Guid.NewGuid(), TenantId = TenantId, EncryptedRefreshToken = [9], ExpiresAt = _time.Now.AddDays(30) };
            db.PendingRevocations.Add(revocation);
            await db.SaveChangesAsync();
            var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
            foreach (var c in new[] { current, older, foreign })
            {
                await log.AppendAsync(c, "message", new JsonObject { ["text"] = "secret " + c });
                await log.AppendAsync(c, "preview", new JsonObject { ["senderAddress"] = "SENDERADDR" });
            }
        }

        using var client = factory.CreateClient();

        // The export view lists both of the user's conversations, never someone else's, and each bundle verifies.
        var list = JsonNode.Parse(await (await client.SendAsync(Req(HttpMethod.Get, RetentionEndpoints.ListPath, token, csrf: false))).Content.ReadAsStringAsync())!;
        var ids = list["conversations"]!.AsArray().Select(c => Guid.Parse((string)c!["conversationId"]!)).ToHashSet();
        Assert.Equal([current, older], ids);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, $"{RetentionEndpoints.ListPath}/{foreign}/evidence", token, csrf: false))).StatusCode);
        var before = JsonNode.Parse(await (await client.SendAsync(Req(HttpMethod.Get, $"{RetentionEndpoints.ListPath}/{older}/evidence", token, csrf: false))).Content.ReadAsStringAsync())!;
        Assert.Contains("secret", before.ToJsonString());
        Assert.True(VerifyBundle(before, Map(await keys.GetPublicKeysAsync(default))).Valid);

        // CSRF guard, and no cookie.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, token, csrf: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, null))).StatusCode);

        var res = await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, token));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(2, (int)body["conversations"]!);
        Assert.Equal(4, (int)body["payloadsDeleted"]!);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
            var map = Map(await keys.GetPublicKeysAsync(default));
            foreach (var c in new[] { current, older })
            {
                Assert.Null((await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == c)).OidcSub);
                var chain = await log.GetChainAsync(c);
                Assert.Equal(2, chain.Count);
                Assert.All(chain, e => { Assert.Null(e.Payload); Assert.Null(e.Salt); Assert.NotEmpty(e.PayloadHash); });
                Assert.True((await log.VerifyAsync(c)).Valid);
                var m = await db.Messages.AsNoTracking().SingleAsync(x => x.ConversationId == c);
                Assert.Equal(("", (string?)null, (string?)null), (m.Content, m.ToolCalls, m.ToolResults));
            }
            var preview = await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.ConversationId == current);
            Assert.Equal(RetentionService.BlankCanonicalJson, preview.CanonicalJson);
            Assert.Equal(ActionPreviewStatus.Completed, preview.Status);
            var step = await db.ActionSteps.AsNoTracking().SingleAsync(s => s.PreviewId == preview.Id);
            Assert.Null(step.UnsignedGroup);
            Assert.Null(step.LimitsRequest);
            Assert.Null(step.SenderAddress);
            Assert.Equal(new string('a', 64), step.SubjectHash);
            Assert.Equal(["TX1"], step.TxIds);
            Assert.Equal("testnet", step.Network);
            var consent = await db.ConsentRequests.AsNoTracking().SingleAsync(x => x.StepId == step.Id);
            Assert.Equal(new string('a', 64), consent.SubjectHash);
            Assert.Contains("pairwise", consent.Receipt);

            // Someone else's conversation is untouched; PendingRevocation rows survive erasure.
            Assert.Equal("someone-else", (await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == foreign)).OidcSub);
            Assert.All(await log.GetChainAsync(foreign), e => Assert.NotNull(e.Payload));
            Assert.Equal("send 5 ALGO to bob", (await db.Messages.AsNoTracking().SingleAsync(x => x.ConversationId == foreign)).Content);
            Assert.True(await db.PendingRevocations.AnyAsync(p => p.Id == revocation.Id));
        }

        // The current conversation's bundle still exports and verifies, with hashes but no text.
        var after = JsonNode.Parse(await (await client.SendAsync(Req(HttpMethod.Get, $"{RetentionEndpoints.ListPath}/{current}/evidence", token, csrf: false))).Content.ReadAsStringAsync())!;
        Assert.DoesNotContain("secret", after.ToJsonString());
        Assert.True(VerifyBundle(after, Map(await keys.GetPublicKeysAsync(default))).Valid);
    }

    [Fact]
    public async Task Erasure_IsRefusedWhileAnActionIsOpen_AndForGuests()
    {
        _time.Now = DateTimeOffset.UtcNow;
        using var keys = new DevEvidenceKeyStore();
        await using var factory = NewFactory(keys);
        var token = SessionService.NewToken();
        var guest = SessionService.NewToken();
        Guid conv;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            conv = await SeedConversationAsync(db, "sub-open", withPreview: true);
            await db.ActionPreviews.Where(p => p.ConversationId == conv)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Proposed));
            var guestConv = await SeedConversationAsync(db, null);
            db.ChatSessions.Add(new ChatSession
            {
                Id = SessionService.IdFromToken(token), ConversationId = conv, Tier = SessionTier.Authenticated, OidcSub = "sub-open", TenantId = TenantId,
                CreatedAt = _time.Now, LastActivityAt = _time.Now, AbsoluteExpiresAt = _time.Now.AddHours(1),
            });
            db.ChatSessions.Add(new ChatSession
            {
                Id = SessionService.IdFromToken(guest), ConversationId = guestConv, Tier = SessionTier.Anonymous, TenantId = TenantId,
                CreatedAt = _time.Now, LastActivityAt = _time.Now, AbsoluteExpiresAt = _time.Now.AddHours(1),
            });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, guest))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, token))).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.Equal("sub-open", (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == conv)).OidcSub);
            Assert.NotNull((await db.ActionSteps.AsNoTracking().SingleAsync()).UnsignedGroup);
        }
    }
}
