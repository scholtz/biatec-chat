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

    /// <summary>The default payload window, so the tests follow the configured default.</summary>
    private static readonly TimeSpan PayloadWindow = new RetentionOptions().Payloads;

    [Fact]
    public void Defaults_KeepPayloadsFiveYears_InsideTheRecordWindow()
    {
        var o = new RetentionOptions();
        Assert.Equal(TimeSpan.FromDays(1826), o.Payloads);
        Assert.True(o.Payloads < o.Records);
    }

    private RetentionService Retention(ChatDbContext db, int batchSize = 500) => new(db, Options.Create(new RetentionOptions { BatchSize = batchSize }), _time);

    private async Task<Guid> SeedConversationAsync(ChatDbContext db, string? sub, bool withPreview = false, bool withSession = false, bool erased = false)
    {
        var now = _time.GetUtcNow();
        var id = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = id, TenantId = TenantId, OidcSub = sub, CreatedAt = now, LastMessageAt = now, ErasedAt = erased ? now : null });
        db.Messages.Add(new Message { ConversationId = id, Seq = 1, Role = MessageRole.User, Content = "send 5 ALGO to bob", ToolCalls = "[{\"name\":\"x\"}]", ToolResults = "[{\"ok\":true}]", CreatedAt = now });
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

    [Fact]
    public async Task Retention_RemovesPayloadsAndTranscriptRowsByTheirOwnAge_InBatches_AndTheChainStillVerifies()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var conv = await SeedConversationAsync(db, "sub-1", withPreview: true);
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "old" });
        await log.AppendAsync(conv, "preview", new JsonObject { ["canonicalJson"] = "{}" });
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "old too" });

        // The conversation stays active: a newer message a day before the window ends must not shield the day-0 rows.
        _time.Now += PayloadWindow - TimeSpan.FromDays(1);
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "newer" });
        db.Messages.Add(new Message { ConversationId = conv, Seq = 2, Role = MessageRole.User, Content = "recent", CreatedAt = _time.Now });
        await db.SaveChangesAsync();
        await db.Conversations.Where(c => c.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastMessageAt, _time.Now));

        // A day before the window ends: nothing is past it yet.
        Assert.Equal(new RetentionResult(0, 0, 0, 0), await Retention(db, batchSize: 1).ApplyAsync(default));

        // A day past the window, batch size 1: one run still drains all three old entries.
        _time.Now += TimeSpan.FromDays(2);
        var r = await Retention(db, batchSize: 1).ApplyAsync(default);
        Assert.Equal(3, r.PayloadsDeleted);
        Assert.Equal(1, r.TranscriptsCleared);
        var chain = await log.GetChainAsync(conv);
        Assert.All(chain.Take(3), e => { Assert.Null(e.Payload); Assert.Null(e.Salt); });
        Assert.NotNull(chain[3].Payload);
        Assert.True((await log.VerifyAsync(conv)).Valid);
        var messages = await db.Messages.AsNoTracking().Where(m => m.ConversationId == conv).OrderBy(m => m.Seq).ToListAsync();
        Assert.Equal(("", (string?)null, (string?)null), (messages[0].Content, messages[0].ToolCalls, messages[0].ToolResults));
        Assert.Equal("recent", messages[1].Content);
        // The preview and its step expired on day 0: blanked by their own age.
        var step = await db.ActionSteps.AsNoTracking().SingleAsync();
        Assert.Null(step.UnsignedGroup);
        Assert.Null(step.SenderAddress);
        Assert.Null(step.LimitsRequest);
        Assert.Equal(["TX1"], step.TxIds);
        Assert.Equal(new string('a', 64), step.SubjectHash);
        Assert.NotNull((await db.ConsentRequests.AsNoTracking().SingleAsync()).Receipt);
        Assert.Equal(RetentionService.BlankCanonicalJson, (await db.ActionPreviews.AsNoTracking().SingleAsync()).CanonicalJson);
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == conv));
        Assert.Equal(new RetentionResult(0, 0, 0, 0), await Retention(db, batchSize: 1).ApplyAsync(default));
    }

    [Fact]
    public async Task Retention_KeepsThePayloadOfAMultiStepActionStillInFlight()
    {
        await using var db = NewContext();
        var conv = await SeedConversationAsync(db, "sub-1", withPreview: true);
        var preview = await db.ActionPreviews.SingleAsync();
        // The preview's own ExpiresAt (the first step's) is long past, but a second step is still pending.
        preview.Status = ActionPreviewStatus.InProgress;
        preview.ExpiresAt = _time.Now - TimeSpan.FromDays(100);
        db.ActionSteps.Add(new ActionStep
        {
            Id = Guid.NewGuid(), PreviewId = preview.Id, Seq = 2, Kind = ActionStepKind.Sign, SubjectHash = new string('b', 64),
            UnsignedGroup = "[\"second\"]", ExpiresAt = _time.Now.AddMinutes(10), Status = StepStatus.Pending,
        });
        await db.SaveChangesAsync();
        await Retention(db).ApplyAsync(default);
        Assert.NotEqual(RetentionService.BlankCanonicalJson, (await db.ActionPreviews.AsNoTracking().SingleAsync()).CanonicalJson);
        Assert.NotNull((await db.ActionSteps.AsNoTracking().SingleAsync(s => s.Seq == 2)).UnsignedGroup);
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == conv));
    }

    [Fact]
    public async Task Retention_RemovesAnonymousConversationsAfter30Days_NotErasedOnes_AndKeepsPendingRevocations()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var anon = new List<Guid>();
        for (var i = 0; i < 3; i++) anon.Add(await SeedConversationAsync(db, null));
        var anonWithSession = await SeedConversationAsync(db, null, withSession: true);
        var erasedFundMoving = await SeedConversationAsync(db, null, withPreview: true, erased: true);
        var erasedPlain = await SeedConversationAsync(db, null, erased: true);
        var signedIn = await SeedConversationAsync(db, "sub-1");
        await log.AppendAsync(anon[0], "message", new JsonObject { ["text"] = "hello" });
        await log.AppendAsync(anon[0], "end", new JsonObject { ["reason"] = "idle" });
        db.PopupAuthRequests.Add(new PopupAuthRequest { Id = Guid.NewGuid(), ConversationId = anon[0], NonceHash = "n", ExpiresAt = _time.Now });
        var revocation = new PendingRevocation { Id = Guid.NewGuid(), TenantId = TenantId, EncryptedRefreshToken = [1, 2, 3], ExpiresAt = _time.Now.AddDays(1) };
        db.PendingRevocations.Add(revocation);
        await db.SaveChangesAsync();

        _time.Now += TimeSpan.FromDays(29);
        Assert.Equal(0, (await Retention(db).ApplyAsync(default)).AnonymousRemoved);

        // Batch size 1: all three anonymous conversations go in one run.
        _time.Now += TimeSpan.FromDays(2);
        var r = await Retention(db, batchSize: 1).ApplyAsync(default);
        Assert.Equal(3, r.AnonymousRemoved);
        Assert.False(await db.Conversations.AnyAsync(c => anon.Contains(c.Id)));
        Assert.False(await db.Messages.AnyAsync(m => anon.Contains(m.ConversationId)));
        Assert.False(await db.PublicConversationStates.AnyAsync(p => anon.Contains(p.ConversationId)));
        Assert.False(await db.PopupAuthRequests.AnyAsync(p => anon.Contains(p.ConversationId)));
        // Its chain stays without payloads and still verifies.
        var chain = await log.GetChainAsync(anon[0]);
        Assert.Equal(2, chain.Count);
        Assert.All(chain, e => Assert.Null(e.Payload));
        Assert.True((await log.VerifyAsync(anon[0])).Valid);
        // A session not yet swept, erased conversations (with or without an action) and a signed-in one all stay.
        foreach (var kept in new[] { anonWithSession, erasedFundMoving, erasedPlain, signedIn })
            Assert.True(await db.Conversations.AnyAsync(c => c.Id == kept));
        Assert.NotNull((await db.ConsentRequests.AsNoTracking().SingleAsync()).Receipt);
        Assert.True(await db.PendingRevocations.AnyAsync(p => p.Id == revocation.Id));
    }

    [Fact]
    public async Task Retention_RemovesRecordsAfter7Years_IncludingOrphanChainsAndAnchorLeaves()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var fundMoving = await SeedConversationAsync(db, null, withPreview: true, erased: true);
        var signedIn = await SeedConversationAsync(db, "sub-1");
        var withSession = await SeedConversationAsync(db, "sub-2", withSession: true);
        var orphan = Guid.NewGuid(); // a chain whose conversation row the anonymous rule already removed
        foreach (var c in new[] { fundMoving, signedIn, withSession, orphan })
            await log.AppendAsync(c, "message", new JsonObject { ["text"] = "x" });
        var anchor = new EvidenceAnchor { Id = Guid.NewGuid(), SequenceNumber = 1, AnchorTxId = "ANCHORTX", MerkleRoot = "root", AnchoredAt = _time.Now };
        db.EvidenceAnchors.Add(anchor);
        var leafIndex = 0;
        foreach (var c in new[] { fundMoving, orphan, withSession })
            db.EvidenceAnchorLeaves.Add(new EvidenceAnchorLeaf { AnchorId = anchor.Id, ConversationId = c, AnchoredSeq = 1, HeadHash = "h", LeafIndex = leafIndex++ });
        var revocation = new PendingRevocation { Id = Guid.NewGuid(), TenantId = TenantId, EncryptedRefreshToken = [1], ExpiresAt = _time.Now.AddDays(1) };
        db.PendingRevocations.Add(revocation);
        await db.SaveChangesAsync();

        // An orphan chain that is old but gets a new entry inside the window stays.
        var recentOrphan = Guid.NewGuid();
        await log.AppendAsync(recentOrphan, "message", new JsonObject { ["text"] = "old" });

        _time.Now += TimeSpan.FromDays(2556);
        Assert.Equal(0, (await Retention(db).ApplyAsync(default)).RecordsRemoved);
        await log.AppendAsync(recentOrphan, "message", new JsonObject { ["text"] = "new" });

        _time.Now += TimeSpan.FromDays(2);
        var r = await Retention(db, batchSize: 1).ApplyAsync(default);
        Assert.Equal(3, r.RecordsRemoved); // fundMoving, signedIn and the orphan chain; withSession waits for the sweeper
        foreach (var gone in new[] { fundMoving, signedIn, orphan })
        {
            Assert.Empty(await log.GetChainAsync(gone));
            Assert.False(await db.Conversations.AnyAsync(c => c.Id == gone));
            Assert.False(await db.EvidenceAnchorLeaves.AnyAsync(l => l.ConversationId == gone));
        }
        Assert.False(await db.ActionPreviews.AnyAsync());
        Assert.False(await db.ConsentRequests.AnyAsync());
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == withSession));
        Assert.True(await db.EvidenceAnchorLeaves.AnyAsync(l => l.ConversationId == withSession));
        Assert.Equal(2, (await log.GetChainAsync(recentOrphan)).Count);
        Assert.True(await db.EvidenceAnchors.AnyAsync(a => a.Id == anchor.Id));
        // Long expired, but PendingRevocation rows are their own service's to purge.
        Assert.True(await db.PendingRevocations.AnyAsync(p => p.Id == revocation.Id));
    }

    [Fact]
    public async Task AdminChain_KeepsItsPayloads_UntilTheRecordWindow()
    {
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using var db = NewContext();
        var log = new EvidenceLog(db, keys, _time);
        var admin = Biatec.Chat.Api.Admin.AdminEvidence.ChainId;
        var conv = await SeedConversationAsync(db, "sub-1");
        await log.AppendAsync(admin, Biatec.Chat.Api.Admin.AdminEvidence.Kind, new JsonObject { ["operator"] = "alice" });
        await log.AppendAsync(conv, "message", new JsonObject { ["text"] = "x" });

        _time.Now += PayloadWindow + TimeSpan.FromDays(1);
        Assert.Equal(1, (await Retention(db).ApplyAsync(default)).PayloadsDeleted); // the conversation's entry only
        var kept = (await log.GetChainAsync(admin)).Single();
        Assert.Contains("alice", kept.Payload);
        Assert.NotNull(kept.Salt);
        Assert.True((await log.VerifyAsync(admin)).Valid);

        _time.Now += TimeSpan.FromDays(2556) - (PayloadWindow + TimeSpan.FromDays(1));
        await Retention(db).ApplyAsync(default);
        Assert.Single(await log.GetChainAsync(admin)); // inside the record window

        _time.Now += TimeSpan.FromDays(2);
        await Retention(db).ApplyAsync(default);
        Assert.Empty(await log.GetChainAsync(admin)); // past it: removed like any orphan chain
    }

    [Fact]
    public async Task HostedService_RunsShortlyAfterStartup_WithoutWaitingAFullInterval()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(Options.Create(new RetentionOptions { StartupDelay = TimeSpan.FromMilliseconds(50), Interval = TimeSpan.FromHours(1) }));
        services.AddScoped<RetentionService>();
        await using var sp = services.BuildServiceProvider();
        var conv = Guid.NewGuid();
        using var keys = new DevEvidenceKeyStore(time: _time);
        await using (var db = NewContext()) await new EvidenceLog(db, keys, _time).AppendAsync(conv, "message", new JsonObject { ["text"] = "x" });
        _time.Now += PayloadWindow + TimeSpan.FromDays(1);

        var hosted = new RetentionHostedService(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptions<RetentionOptions>>(),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<RetentionHostedService>.Instance);
        await hosted.StartAsync(default);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            bool cleared;
            do
            {
                await Task.Delay(100);
                await using var db = NewContext();
                cleared = await db.EvidenceEntries.AnyAsync(e => e.ConversationId == conv && e.Payload == null);
            } while (!cleared && DateTime.UtcNow < deadline);
            Assert.True(cleared);
        }
        finally { await hosted.StopAsync(default); }
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
            AnchorRef = (string?)e["anchorRef"],
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

    // BCHAT-013: erasure waits for an in-flight evidence append or message store of the same conversation (they share the per-conversation lock).
    [Fact]
    public async Task Erase_WaitsForTheConversationLock_SoAnInFlightWriteLandsBeforeTheBlanking()
    {
        Guid conv;
        await using (var seed = NewContext()) { conv = await SeedConversationAsync(seed, "sub-lock"); await seed.SaveChangesAsync(); }

        await using var holder = NewContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await ConversationLock.AcquireAsync(holder, conv, default); // the in-flight turn is writing

        var erase = Task.Run(async () =>
        {
            await using var db = NewContext();
            return await Retention(db).EraseAsync(TenantId, "sub-lock", null, default);
        });
        // Deterministic: wait until PostgreSQL reports an advisory lock request that is not granted (erasure waiting on the writer).
        await using (var admin = new NpgsqlConnection(AdminCs))
        {
            await admin.OpenAsync();
            var waiting = false;
            for (var i = 0; i < 100 && !waiting; i++)
            {
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted", admin);
                waiting = (long)(await cmd.ExecuteScalarAsync())! > 0;
                if (!waiting) await Task.Delay(100);
            }
            Assert.True(waiting, "erasure must wait for the writer's lock");
        }
        Assert.False(erase.IsCompleted);

        await held.CommitAsync(); // the write is done
        Assert.Equal(ErasureOutcome.Erased, (await erase.WaitAsync(TimeSpan.FromSeconds(15))).Outcome);
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

        // The erased conversation's bundle still verifies, with hashes but no text.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var chain = await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().GetChainAsync(current);
            var after = await Biatec.Chat.Api.Evidence.Export.EvidenceBundleBuilder.BuildAsync(current, chain, keys, null, DateTimeOffset.UtcNow, default);
            Assert.DoesNotContain("secret", after.ToJsonString());
            Assert.True(VerifyBundle(after, Map(await keys.GetPublicKeysAsync(default))).Valid);
        }

        // The session moved to a fresh conversation of the same user: what it writes next can be erased again.
        var fresh = Guid.Parse((string)body["conversationId"]!);
        Assert.NotEqual(current, fresh);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.Equal(fresh, (await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == SessionService.IdFromToken(token))).ConversationId);
            var old = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == current);
            Assert.NotNull(old.ErasedAt);
            Assert.NotNull(old.EndedAt);
            Assert.Equal(sub, (await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == fresh)).OidcSub);
            // Chat again (written the way the orchestrator and the recorder write).
            db.Messages.Add(new Message { ConversationId = fresh, Seq = 1, Role = MessageRole.User, Content = "new secret", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IEvidenceLog>().AppendAsync(fresh, "message", new JsonObject { ["text"] = "new secret" });
        }
        var listed = JsonNode.Parse(await (await client.SendAsync(Req(HttpMethod.Get, RetentionEndpoints.ListPath, token, csrf: false))).Content.ReadAsStringAsync())!;
        Assert.Equal([fresh], listed["conversations"]!.AsArray().Select(c => Guid.Parse((string)c!["conversationId"]!)));

        var again = await client.SendAsync(Req(HttpMethod.Post, RetentionEndpoints.ErasePath, token));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
            Assert.Equal("", (await db.Messages.AsNoTracking().SingleAsync(x => x.ConversationId == fresh)).Content);
            Assert.All(await log.GetChainAsync(fresh), e => Assert.Null(e.Payload));
            Assert.True((await log.VerifyAsync(fresh)).Valid);
            Assert.Null((await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == fresh)).OidcSub);
        }
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
            // A multi-step action: the preview's own ExpiresAt (the first step's) has passed, a later step is pending.
            var previewId = (await db.ActionPreviews.SingleAsync(p => p.ConversationId == conv)).Id;
            await db.ActionPreviews.Where(p => p.Id == previewId).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, ActionPreviewStatus.InProgress).SetProperty(p => p.ExpiresAt, _time.Now.AddMinutes(-10)));
            db.ActionSteps.Add(new ActionStep
            {
                Id = Guid.NewGuid(), PreviewId = previewId, Seq = 2, Kind = ActionStepKind.Sign, SubjectHash = new string('b', 64),
                UnsignedGroup = "[\"second\"]", ExpiresAt = _time.Now.AddMinutes(10), Status = StepStatus.Pending,
            });
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
            Assert.All(await db.ActionSteps.AsNoTracking().ToListAsync(), st => Assert.NotNull(st.UnsignedGroup));
        }
    }
}
