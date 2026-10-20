using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Signing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Biatec.Chat.Tests.Signing;

/// <summary>
/// T-079: POST /chat/previews/{id}/confirm reports the registered page Origin (web vs dex, one backend host) to Biatec
/// OIDC's sign endpoint. Needs the docker-compose postgres; Biatec OIDC and the vault are fakes.
/// </summary>
public sealed class ConfirmOriginEndpointTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    /// <summary>The fake wallet endpoint: records the request and refuses, so nothing is submitted.</summary>
    private sealed class FakeOidc : IWalletSigningClient
    {
        public readonly List<WalletSignRequest> Calls = [];
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(request);
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Rejected(403, "access_denied"));
        }
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
    private readonly MemoryVault _vault = new();
    private readonly FakeOidc _oidc = new();
    private readonly Account _user = new();
    private WebApplicationFactory<Program> _factory = null!;

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
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<ITokenVault>();
                s.AddSingleton<ITokenVault>(_vault);
                s.RemoveAll<IWalletSigningClient>();
                s.AddSingleton<IWalletSigningClient>(_oidc);
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
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

    private static HttpRequestMessage Req(HttpMethod m, string path, string origin, string? cookie, object? body = null)
    {
        var r = new HttpRequestMessage(m, "http://chat.biatec.io" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        r.Headers.TryAddWithoutValidation("Origin", origin);
        r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", $"{SessionPolicy.CookieName}={cookie}");
        return r;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(r);
    }

    private async Task<(string Cookie, Guid ConversationId)> NewSignedInSessionAsync(string origin)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/session", origin, null));
        var sc = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(SessionPolicy.CookieName + "="));
        var token = sc[(SessionPolicy.CookieName.Length + 1)..].Split(';')[0];
        var id = SessionService.IdFromToken(token);
        await using var db = NewContext();
        await db.ChatSessions.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Tier, SessionTier.Authenticated));
        var session = await db.ChatSessions.AsNoTracking().SingleAsync(x => x.Id == id);
        _vault.Items[id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS", AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), RefreshToken = "REFRESH",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = session.AbsoluteExpiresAt,
        };
        return (token, session.ConversationId);
    }

    private async Task<(Guid PreviewId, string GroupHash)> SeedAsync(Guid conversationId)
    {
        var pay = new PaymentTransaction
        {
            Sender = _user.Address, Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
            Amount = 5_000_000, Fee = 1000, FirstValid = 710399, LastValid = 711399, GenesisId = "testnet-v1.0",
            GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")), Note = Encoding.UTF8.GetBytes("t"),
        };
        var group = AlgorandGroup.Ensure([pay]);
        var exp = DateTimeOffset.UtcNow.AddMinutes(20);
        await using var db = NewContext();
        var preview = new ActionPreview
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Capability = "wallet.transfer", CanonicalJson = "{\"intent\":\"send\"}",
            Status = ActionPreviewStatus.Proposed, ExpiresAt = exp,
        };
        db.ActionPreviews.Add(preview);
        db.ActionSteps.Add(new ActionStep
        {
            Id = Guid.NewGuid(), PreviewId = preview.Id, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = group.GroupHash,
            UnsignedGroup = JsonSerializer.Serialize(group.ToBase64()), SenderAddress = _user.Address.EncodeAsString(),
            Network = "algorand-testnet", ExpiresAt = exp, Status = StepStatus.Pending,
        });
        await db.SaveChangesAsync();
        return (preview.Id, group.GroupHash);
    }

    [Theory]
    [InlineData("https://dex.biatec.io")]
    [InlineData("https://web.biatec.io")]
    public async Task Confirm_ReportsTheRequestsRegisteredOrigin_NotTheBackendHost(string origin)
    {
        var (cookie, conv) = await NewSignedInSessionAsync(origin);
        var (previewId, hash) = await SeedAsync(conv);

        var r = await SendAsync(Req(HttpMethod.Post, $"/chat/previews/{previewId}/confirm", origin, cookie, new { groupHash = hash }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); // the fake wallet endpoint refused after recording
        Assert.Equal(origin, Assert.Single(_oidc.Calls).Origin);
    }

    [Fact]
    public async Task Confirm_FromAnUnregisteredOrigin_NeverReachesSigning()
    {
        var (cookie, conv) = await NewSignedInSessionAsync("https://dex.biatec.io");
        var (previewId, hash) = await SeedAsync(conv);

        var r = await SendAsync(Req(HttpMethod.Post, $"/chat/previews/{previewId}/confirm", "https://evil.example", cookie, new { groupHash = hash }));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(_oidc.Calls);
    }
}
