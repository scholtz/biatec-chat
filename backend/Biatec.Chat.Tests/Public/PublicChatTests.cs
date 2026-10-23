using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Tests.Public;

/// <summary>Needs the docker-compose postgres. Bedrock is scripted; Redis is unreachable so the limiter uses its in-memory fallback.</summary>
public sealed class PublicChatTests : IAsyncLifetime
{
    private const string RawIp = "203.0.113.77";

    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Replies "reply-N" to the Nth model call.</summary>
    private sealed class CountingModel : IModelClient
    {
        public int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var text = "reply-" + Interlocked.Increment(ref Calls);
            await Task.Yield();
            yield return new ModelTextDelta(text);
            yield return new ModelMessageStop("end_turn", new ModelMessage { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = text }] });
        }
    }

    private sealed class CaptureLogger : ILoggerProvider
    {
        public readonly List<string> Lines = [];
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }
        private sealed class L(CaptureLogger p) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (p.Lines) p.Lines.Add(formatter(state, exception) + exception);
            }
        }
    }

    private sealed class SetIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse(RawIp); return n(ctx); });
            next(app);
        };
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeTime _time = new();
    private readonly CountingModel _model = new();
    private readonly CaptureLogger _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using (var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options)) await db.Database.MigrateAsync();
        Build(ipPermits: 100);
    }

    private void Build(int ipPermits, Dictionary<string, string?>? extra = null)
    {
        _factory?.Dispose();
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
                ["PublicChat:IpPermitsPerMinute"] = ipPermits.ToString(),
            }.Concat(extra ?? new Dictionary<string, string?>())));
            b.ConfigureLogging(l => l.AddProvider(_logs));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_time);
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient>(_model);
                s.AddSingleton<IStartupFilter, SetIpFilter>();
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private sealed record Reply(HttpStatusCode Status, string Body, string? Conversation, string? Continuation, string? Code, string? RetryAfter);

    private async Task<Reply> ChatAsync(HttpClient c, string? conversation, string? token, string message = "hello", string origin = "https://elsewhere.example")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/public/chat")
        {
            Content = JsonContent.Create(new { message, conversationId = conversation, continuationToken = token }),
        };
        req.Headers.TryAddWithoutValidation("Origin", origin);
        var r = await c.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        string? code = null;
        if (r.StatusCode != HttpStatusCode.OK && body.StartsWith('{')) code = JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();
        return new Reply(r.StatusCode, body,
            r.Headers.TryGetValues("X-Biatec-Conversation", out var a) ? a.Single() : null,
            r.Headers.TryGetValues("X-Biatec-Continuation", out var b) ? b.Single() : null,
            code, r.Headers.TryGetValues("Retry-After", out var ra) ? ra.Single() : null);
    }

    [Fact]
    public async Task Public_FirstMessage_StartsConversationAndIssuesCapabilityAndToken()
    {
        using var c = _factory.CreateClient();
        var r = await ChatAsync(c, null, null);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Conversation));
        Assert.False(string.IsNullOrEmpty(r.Continuation));
        Assert.Contains("reply-1", r.Body);

        // The capability and token are stored only as hashes.
        using var scope = _factory.Services.CreateScope();
        var st = await scope.ServiceProvider.GetRequiredService<ChatDbContext>().PublicConversationStates.SingleAsync();
        Assert.NotEqual(r.Conversation, st.CapabilityHash);
        Assert.NotEqual(r.Continuation, st.ContinuationTokenHash);
    }

    // BCHAT-039: a turn held by another replica answers busy and does not spend the continuation token.
    [Fact]
    public async Task Public_ATurnHeldByAnotherReplica_IsBusy_AndKeepsTheContinuationToken()
    {
        using var c = _factory.CreateClient();
        var first = await ChatAsync(c, null, null);
        Assert.Equal(HttpStatusCode.OK, first.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var conversationId = (await db.PublicConversationStates.SingleAsync()).ConversationId;
        var held = await Biatec.Chat.Api.Chat.ConversationTurnLock.TryAcquireAsync(db, conversationId, default);
        Assert.NotNull(held);

        var busy = await ChatAsync(c, first.Conversation, first.Continuation, "second");
        Assert.Equal(HttpStatusCode.Conflict, busy.Status);
        Assert.Equal("turn_in_progress", busy.Code);

        await held!.DisposeAsync();
        var again = await ChatAsync(c, first.Conversation, first.Continuation, "second");
        Assert.Equal(HttpStatusCode.OK, again.Status); // the same token still works: the busy refusal did not consume it
    }

    [Fact]
    public async Task Public_ReplyRequiresLatestContinuationToken()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);

        var missing = await ChatAsync(c, r1.Conversation, null);
        Assert.Equal(HttpStatusCode.Conflict, missing.Status);
        Assert.Equal("continuation_required", missing.Code);

        var wrongCapability = await ChatAsync(c, "AAAAAAAAAAAAAAAAAAAAAA", r1.Continuation);
        Assert.Equal(HttpStatusCode.NotFound, wrongCapability.Status);

        var guessed = await ChatAsync(c, r1.Conversation, "not-a-real-token-not-a-real-token-1234");
        Assert.Equal(HttpStatusCode.NotFound, guessed.Status);

        var r2 = await ChatAsync(c, r1.Conversation, r1.Continuation, "second");
        Assert.Equal(HttpStatusCode.OK, r2.Status);
        Assert.NotEqual(r1.Continuation, r2.Continuation);
        var r3 = await ChatAsync(c, r1.Conversation, r2.Continuation, "third");
        Assert.Equal(HttpStatusCode.OK, r3.Status);

        // r1's token is now two replies old: neither current nor previous.
        var stale = await ChatAsync(c, r1.Conversation, r1.Continuation, "stale");
        Assert.Equal(HttpStatusCode.NotFound, stale.Status);
        Assert.Equal(3, _model.Calls);
    }

    [Fact]
    public async Task Public_LostReplyIsReplayedOnce()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        var r2 = await ChatAsync(c, r1.Conversation, r1.Continuation, "second"); // the client loses this reply, and its token
        Assert.Contains("reply-2", r2.Body);

        var replay = await ChatAsync(c, r1.Conversation, r1.Continuation, "second");
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Contains("reply-2", replay.Body);
        Assert.Equal(2, _model.Calls); // the model was not asked again
        Assert.NotEqual(r2.Continuation, replay.Continuation);

        var again = await ChatAsync(c, r1.Conversation, r1.Continuation, "second");
        Assert.Equal(HttpStatusCode.NotFound, again.Status);

        // The conversation continues with the token the replay issued.
        var next = await ChatAsync(c, r1.Conversation, replay.Continuation, "third");
        Assert.Equal(HttpStatusCode.OK, next.Status);
        Assert.Contains("reply-3", next.Body);
    }

    [Fact]
    public async Task Public_ConcurrentUseOfOneToken_OnlyOneWins()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ChatAsync(c, r1.Conversation, r1.Continuation, "race")));
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK && r.Body.Contains("reply-")));
    }

    private HttpRequestMessage Adopt(string? conversation, string? token, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/session/adopt")
        {
            Content = JsonContent.Create(new { conversationId = conversation, continuationToken = token }),
        };
        req.Headers.TryAddWithoutValidation("Origin", "https://web.biatec.io");
        req.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        req.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        if (cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        return req;
    }

    [Fact]
    public async Task Public_AdoptedConversationIsRefusedOnPublicEndpoint()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        var r2 = await ChatAsync(c, r1.Conversation, r1.Continuation);

        // A stale token cannot adopt.
        var stale = await c.SendAsync(Adopt(r1.Conversation, r1.Continuation));
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        // Without the CSRF header the cookie endpoint refuses outright.
        var noCsrf = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/session/adopt") { Content = JsonContent.Create(new { conversationId = r1.Conversation, continuationToken = r2.Continuation }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(noCsrf)).StatusCode);

        var ok = await c.SendAsync(Adopt(r1.Conversation, r2.Continuation));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), v => v.StartsWith(SessionPolicy.CookieName));

        var after = await ChatAsync(c, r1.Conversation, r2.Continuation);
        Assert.Equal(HttpStatusCode.Gone, after.Status);
        Assert.Equal("conversation_adopted", after.Code);

        // And nobody can adopt it twice.
        var twice = await c.SendAsync(Adopt(r1.Conversation, r2.Continuation));
        Assert.Equal(HttpStatusCode.Gone, twice.StatusCode);

        // The cookie session now owns the conversation.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var state = await db.PublicConversationStates.SingleAsync();
        Assert.NotNull(state.AdoptedAt);
        Assert.Contains(await db.ChatSessions.ToListAsync(), s => s.ConversationId == state.ConversationId);
    }

    [Fact]
    public async Task Public_AuthenticatedConversationIsRefusedOnPublicEndpoint()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Conversations.ExecuteUpdateAsync(u => u.SetProperty(x => x.OidcSub, "user-1"));
        }
        var r = await ChatAsync(c, r1.Conversation, r1.Continuation);
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal("conversation_authenticated", r.Code);
        var adopt = await c.SendAsync(Adopt(r1.Conversation, r1.Continuation));
        Assert.Equal(HttpStatusCode.Forbidden, adopt.StatusCode);
    }

    [Fact]
    public async Task Public_IdleConversationIsClosedWithDistinctCode()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        _time.Now += TimeSpan.FromMinutes(31);
        var r = await ChatAsync(c, r1.Conversation, r1.Continuation);
        Assert.Equal(HttpStatusCode.Gone, r.Status);
        Assert.Equal("conversation_idle_closed", r.Code);
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Conversations.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task Public_RateLimitTriggers_AndRawIpIsNeverLogged()
    {
        Build(ipPermits: 2);
        using var c = _factory.CreateClient();
        var a = await ChatAsync(c, null, null);
        var b = await ChatAsync(c, a.Conversation, a.Continuation);
        var limited = await ChatAsync(c, b.Conversation, b.Continuation);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("rate_limited", limited.Code);
        Assert.NotNull(limited.RetryAfter);

        // The window moves on, the limit lifts, and the rejected request did not consume the token.
        _time.Now += TimeSpan.FromMinutes(2);
        var ok = await ChatAsync(c, b.Conversation, b.Continuation);
        Assert.Equal(HttpStatusCode.OK, ok.Status);

        lock (_logs.Lines) Assert.DoesNotContain(_logs.Lines, l => l.Contains(RawIp));
    }

    [Fact]
    public async Task Public_Origin_ReportsRegistration()
    {
        using var c = _factory.CreateClient();
        async Task<bool> Registered(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "http://chat.biatec.io/public/origin");
            req.Headers.TryAddWithoutValidation("Origin", origin);
            var r = await c.SendAsync(req);
            Assert.Equal("*", r.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.False(r.Headers.Contains("Set-Cookie"));
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("registered").GetBoolean();
        }
        Assert.True(await Registered("https://web.biatec.io"));
        Assert.False(await Registered("https://evil.example"));
    }

    [Fact]
    public async Task Public_Chat_SetsNoCookie()
    {
        using var c = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/public/chat") { Content = JsonContent.Create(new { message = "hi" }) };
        req.Headers.TryAddWithoutValidation("Origin", "https://elsewhere.example");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Equal("*", r.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    private async Task<HttpStatusCode> ChatFromAsync(HttpClient c, string? xff, string origin = "https://elsewhere.example")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/public/chat") { Content = JsonContent.Create(new { message = "hi" }) };
        req.Headers.TryAddWithoutValidation("Origin", origin);
        if (xff is not null) req.Headers.TryAddWithoutValidation("X-Forwarded-For", xff);
        return (await c.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Public_TrustedProxy_ForwardedForSelectsTheIpBucket()
    {
        Build(ipPermits: 1, new() { ["ForwardedHeaders:KnownProxies:0"] = RawIp });
        using var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ChatFromAsync(c, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.2"));
    }

    [Fact]
    public async Task Public_UntrustedPeer_ForwardedForIsIgnored()
    {
        Build(ipPermits: 1);
        using var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ChatFromAsync(c, "198.51.100.2"));
    }

    [Fact]
    public async Task Public_UnregisteredOrigin_GetsNoSharedOriginBucket()
    {
        Build(ipPermits: 100, new() { ["PublicChat:OriginPermitsPerMinute"] = "2", ["ForwardedHeaders:KnownProxies:0"] = RawIp });
        using var c = _factory.CreateClient();
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, $"198.51.100.{i + 1}", "https://elsewhere.example"));
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.50", "https://web.biatec.io"));
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.51", "https://web.biatec.io"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ChatFromAsync(c, "198.51.100.52", "https://web.biatec.io"));
    }

    [Fact]
    public async Task Public_NoOriginRequests_ShareTheirOwnSmallBucket()
    {
        Build(ipPermits: 100, new() { ["PublicChat:NoOriginPermitsPerMinute"] = "2", ["ForwardedHeaders:KnownProxies:0"] = RawIp });
        using var c = _factory.CreateClient();
        async Task<HttpStatusCode> NoOrigin(string xff)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/public/chat") { Content = JsonContent.Create(new { message = "hi" }) };
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", xff);
            return (await c.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await NoOrigin("198.51.100.1"));
        Assert.Equal(HttpStatusCode.OK, await NoOrigin("198.51.100.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await NoOrigin("198.51.100.3"));
        Assert.Equal(HttpStatusCode.OK, await ChatFromAsync(c, "198.51.100.4"));
    }

    [Fact]
    public async Task Public_Adopt_RefusesWhenTheCookieSessionAlreadyHasAConversation()
    {
        using var c = _factory.CreateClient();
        var r1 = await ChatAsync(c, null, null);
        var r2 = await ChatAsync(c, null, null);
        var first = await c.SendAsync(Adopt(r1.Conversation, r1.Continuation));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cookie = first.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(SessionPolicy.CookieName)).Split(';')[0];

        var second = await c.SendAsync(Adopt(r2.Conversation, r2.Continuation, cookie));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var adoptedId = (await db.PublicConversationStates.SingleAsync(s => s.AdoptedAt != null)).ConversationId;
        Assert.Equal(adoptedId, (await db.ChatSessions.SingleAsync()).ConversationId);
        // The empty placeholder conversation the session started with was ended when r1 replaced it.
        Assert.Single(await db.Conversations.Where(x => x.EndedAt != null).ToListAsync());
        // The refused conversation was not retired; its public identifier still works.
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(c, r2.Conversation, r2.Continuation)).Status);
    }
}
