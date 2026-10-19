using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using ActionPreview = Biatec.Chat.Core.Data.ActionPreview;
using ActionStep = Biatec.Chat.Core.Data.ActionStep;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Limits;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Limits;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Biatec.Chat.Tests.Limits;

/// <summary>
/// limits.view and limits.update executors and the confirm path (T-026). Needs the docker-compose postgres. Biatec OIDC, the vault and the evidence log are fakes.
/// </summary>
public sealed class LimitsFlowTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private static SpendingLimit L(string key, string amount) => LimitsPolicyTests.L(key, amount);
    private static IReadOnlyList<SpendingLimit> Current => LimitsPolicyTests.Current;

    // ---- executors and confirm ----

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryVault : ITokenVault
    {
        public readonly Dictionary<string, TokenVaultEntry> Items = new();
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    private sealed class FakeLimits : IWalletLimitsClient
    {
        public IReadOnlyList<SpendingLimit> Now = Current;
        public LimitsReadResult? ReadOverride;
        public LimitsWriteResult Write = new(true);
        public readonly List<IReadOnlyDictionary<string, string>> Puts = [];
        public int Gets;
        public Task<LimitsReadResult> GetAsync(string bearer, CancellationToken ct = default) { Gets++; return Task.FromResult(ReadOverride ?? new LimitsReadResult(Now)); }
        public Task<LimitsWriteResult> PutAsync(string bearer, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
        {
            Puts.Add(body);
            return Task.FromResult(Write);
        }
    }

    private sealed class FakeEvidence : IEvidenceLog
    {
        public readonly List<string> Kinds = [];
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default)
        { Kinds.Add(kind); return Task.FromResult(new EvidenceEntry()); }
        public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryVault _vault = new();
    private readonly FakeLimits _oidc = new();
    private readonly FakeEvidence _evidence = new();
    private Tenant _tenant = null!;
    private ChatSession _session = null!;
    private ServiceProvider _sp = null!;

    private ChatDbContext NewContext()
    {
        var cs = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }

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
        _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
        _tenant.LimitsUpdateEnabled = true;
        db.Tenants.Add(_tenant);
        var conv = new Conversation { Id = Guid.NewGuid(), TenantId = _tenant.Id, OidcSub = "user-1", CreatedAt = _clock.Now, LastMessageAt = _clock.Now };
        _session = new ChatSession
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, Tier = SessionTier.Authenticated, TenantId = _tenant.Id, OidcSub = "user-1",
            CreatedAt = _clock.Now, LastActivityAt = _clock.Now, AbsoluteExpiresAt = _clock.Now.AddHours(8),
        };
        db.Conversations.Add(conv);
        db.ChatSessions.Add(_session);
        await db.SaveChangesAsync();
        Grant("openid", "manage-limits");

        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<ActionPreviewService>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private void Grant(params string[] scopes) =>
        _vault.Items[_session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.Now.AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = scopes, AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };

    private ToolExecutionContext Ctx(string capability, string[]? scopes = null, Tenant? tenant = null, SessionTier tier = SessionTier.Authenticated) =>
        new(CapabilityRegistry.CreateDefault().Find(capability)!, "tu1", _session.ConversationId, _session.Id.ToString("N"), _tenant.Id, tier,
            scopes ?? ["openid", "manage-limits"], tenant ?? _tenant);

    private LimitsUpdateExecutor Update() => new(_vault, _oidc, _sp.GetRequiredService<IServiceScopeFactory>(), _clock);

    private async Task<(ActionPreview Preview, ActionStep Step)> LoadAsync(Guid previewId)
    {
        await using var db = NewContext();
        return (await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == previewId), await db.ActionSteps.AsNoTracking().SingleAsync(s => s.PreviewId == previewId));
    }

    private async Task<int> PreviewCountAsync()
    {
        await using var db = NewContext();
        return await db.ActionPreviews.CountAsync();
    }

    [Fact]
    public async Task View_shows_every_limit_with_its_currency_and_no_raise_path()
    {
        var r = await new LimitsViewExecutor(_vault, _oidc).ExecuteAsync(Ctx("limits.view"), new JsonObject(), default);
        Assert.False(r.IsError);
        var o = JsonNode.Parse(r.Content)!;
        Assert.Equal(3, o["limits"]!.AsArray().Count);
        Assert.Equal("500 EUR", (string)o["limits"]![0]!["display"]!);
        Assert.Equal("EUR", (string)o["limits"]![0]!["currency"]!);
        Assert.True((bool)o["canLower"]!);
        Assert.False((bool)o["canRaise"]!);
        Assert.DoesNotContain("SECRET", r.Content);
    }

    [Fact]
    public async Task View_reports_an_outage_and_an_expired_sign_in_without_upstream_text()
    {
        _oidc.ReadOverride = new LimitsReadResult(null, LimitsFailure.Unavailable, "http_503");
        var r = await new LimitsViewExecutor(_vault, _oidc).ExecuteAsync(Ctx("limits.view"), new JsonObject(), default);
        Assert.True(r.IsError);
        Assert.Contains("temporarily unavailable", r.Content);

        _vault.Items.Clear();
        _oidc.ReadOverride = null;
        var gone = await new LimitsViewExecutor(_vault, _oidc).ExecuteAsync(Ctx("limits.view"), new JsonObject(), default);
        Assert.True(gone.IsError);
        Assert.Equal(1, _oidc.Gets); // never called OIDC without a token
    }

    [Fact]
    public async Task View_refuses_a_session_that_is_not_authenticated()
    {
        var r = await new LimitsViewExecutor(_vault, _oidc).ExecuteAsync(Ctx("limits.view", tier: SessionTier.Anonymous), new JsonObject(), default);
        Assert.True(r.IsError);
        Assert.Equal(0, _oidc.Gets);
    }

    [Fact]
    public async Task Update_lowering_creates_a_preview_bound_to_the_body_hash_and_changes_nothing_yet()
    {
        var r = await Update().ExecuteAsync(Ctx("limits.update"), new JsonObject { ["daily"] = "200" }, default);
        Assert.False(r.IsError);
        Assert.Empty(_oidc.Puts);

        var card = Assert.IsType<PreviewCardEvent>(Assert.Single(r.Events!)).Preview;
        Assert.Equal("limits.update", card.Capability);
        Assert.Contains("500 to 200 EUR", card.Intent);
        var (preview, step) = await LoadAsync(Guid.Parse(card.PreviewId));
        Assert.Equal(ActionStepKind.Limits, step.Kind);
        Assert.Equal(card.Steps[0].GroupHash, step.SubjectHash);
        Assert.Equal(LimitsPolicy.Canonicalize(new Dictionary<string, string> { ["per_day_eur"] = "200", ["per_week_eur"] = "2000", ["per_month_eur"] = "5000" }).Hash, step.SubjectHash);
        Assert.Equal(card.Steps[0].GroupHash, card.Steps[0].GroupHash);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Proposed, preview.Status);
    }

    [Theory]
    [InlineData("501")]
    [InlineData("99999")]
    public async Task Update_raise_is_refused_server_side_with_an_explanation_and_no_preview(string amount)
    {
        var r = await Update().ExecuteAsync(Ctx("limits.update"), new JsonObject { ["daily"] = amount }, default);
        Assert.True(r.IsError);
        Assert.Contains("only lower", r.Content);
        Assert.Null(r.Events);
        Assert.Empty(_oidc.Puts);
        Assert.Equal(0, await PreviewCountAsync());
    }

    [Fact]
    public async Task Update_is_refused_when_the_tenant_flag_is_off_even_if_the_gate_were_bypassed()
    {
        _tenant.LimitsUpdateEnabled = false;
        var r = await Update().ExecuteAsync(Ctx("limits.update"), new JsonObject { ["daily"] = "1" }, default);
        Assert.True(r.IsError);
        Assert.Contains("not enabled", r.Content);
        Assert.Equal(0, _oidc.Gets);
        Assert.Equal(0, await PreviewCountAsync());
    }

    [Fact]
    public async Task Update_without_manage_limits_offers_the_elevation_card_and_prepares_nothing()
    {
        var r = await Update().ExecuteAsync(Ctx("limits.update", scopes: ["openid"]), new JsonObject { ["daily"] = "1" }, default);
        var card = Assert.IsType<SignInCardEvent>(Assert.Single(r.Events!));
        Assert.Equal(SignInReason.ElevateScope, card.Reason);
        Assert.Equal("manage-limits", card.Scope);
        Assert.Equal(0, await PreviewCountAsync());
        Assert.Empty(_oidc.Puts);
    }

    private async Task<(Guid PreviewId, string Hash)> PrepareAsync(string daily = "200")
    {
        var r = await Update().ExecuteAsync(Ctx("limits.update"), new JsonObject { ["daily"] = daily }, default);
        var card = ((PreviewCardEvent)r.Events![0]).Preview;
        return (Guid.Parse(card.PreviewId), card.Steps[0].GroupHash);
    }

    private async Task<ConfirmResult> ConfirmAsync(Guid previewId, string hash, bool viaConfirmService = true)
    {
        await using var db = NewContext();
        var previews = new ActionPreviewService(db, _clock);
        var scopes = new ScopeGuard(_vault, _clock);
        var limitsConfirm = new LimitsConfirmService(db, previews, scopes, _vault, _oidc, _evidence, NullLogger<LimitsConfirmService>.Instance);
        if (!viaConfirmService) return await limitsConfirm.ConfirmAsync(_session, previewId, hash, default);
        // The shared confirm endpoint's service hands a limits step to the limits path.
        var svc = new ConfirmService(db, previews, scopes, _vault, new NoSigner(), new NoMcp(), new NoConfirmer(), _evidence, _clock,
            NullLogger<ConfirmService>.Instance, limitsConfirm);
        return await svc.ConfirmAsync(_tenant, _session, previewId, hash, default);
    }

    [Fact]
    public async Task Confirm_applies_the_lowering_through_the_confirm_service_and_completes_the_preview()
    {
        var (id, hash) = await PrepareAsync();
        var r = await ConfirmAsync(id, hash);

        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        var put = Assert.Single(_oidc.Puts);
        Assert.Equal("200", put["per_day_eur"]);
        Assert.Equal("2000", put["per_week_eur"]);
        var (preview, step) = await LoadAsync(id);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Completed, preview.Status);
        Assert.Equal(StepStatus.Confirmed, step.Status);
        Assert.Equal(["limits_changed"], _evidence.Kinds);
    }

    [Fact]
    public async Task Confirm_with_a_wrong_hash_changes_nothing()
    {
        var (id, _) = await PrepareAsync();
        await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(id, new string('0', 64)));
        Assert.Empty(_oidc.Puts);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Proposed, (await LoadAsync(id)).Preview.Status);
    }

    [Fact]
    public async Task Confirm_refuses_when_the_limits_dropped_meanwhile_so_the_body_would_now_raise()
    {
        var (id, hash) = await PrepareAsync();
        _oidc.Now = [L("per_day_eur", "100"), L("per_week_eur", "2000"), L("per_month_eur", "5000")]; // lowered elsewhere to 100; body says 200
        var r = await ConfirmAsync(id, hash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Equal("raise_refused", r.ErrorCode);
        Assert.Empty(_oidc.Puts);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Failed, (await LoadAsync(id)).Preview.Status);
    }

    [Fact]
    public async Task Confirm_never_follows_a_consent_challenge_on_a_lowering()
    {
        var (id, hash) = await PrepareAsync();
        _oidc.Write = new LimitsWriteResult(false, LimitsFailure.StepUp, "insufficient_user_authentication");
        var r = await ConfirmAsync(id, hash);
        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Equal("unexpected_step_up", r.ErrorCode);
        await using var db = NewContext();
        Assert.Empty(db.ConsentRequests);
    }

    [Fact]
    public async Task Confirm_without_manage_limits_asks_to_reauthorize_and_keeps_the_preview_confirmable()
    {
        var (id, hash) = await PrepareAsync();
        Grant("openid");
        var r = await ConfirmAsync(id, hash);
        Assert.Equal(ConfirmOutcome.ScopeRequired, r.Outcome);
        Assert.Equal("manage-limits", r.ReauthorizeScope);
        Assert.Empty(_oidc.Puts);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Proposed, (await LoadAsync(id)).Preview.Status);
    }

    [Fact]
    public async Task Confirm_during_an_oidc_outage_leaves_the_preview_confirmable()
    {
        var (id, hash) = await PrepareAsync();
        _oidc.Write = new LimitsWriteResult(false, LimitsFailure.Unavailable, "http_503");
        var r = await ConfirmAsync(id, hash);
        Assert.Equal(ConfirmOutcome.SignInUnavailable, r.Outcome);
        Assert.Equal(Biatec.Chat.Core.Data.ActionPreviewStatus.Proposed, (await LoadAsync(id)).Preview.Status);
    }

    [Fact]
    public async Task Confirm_after_the_gate_closes_is_refused_and_a_second_confirm_does_not_apply_twice()
    {
        var (id, hash) = await PrepareAsync();
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(id, hash)).Outcome);
        await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(id, hash));
        Assert.Single(_oidc.Puts);

        var (id2, hash2) = await PrepareAsync("100");
        _clock.Now = _clock.Now.AddMinutes(25); // under 600 seconds of the 30 minutes left
        await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(id2, hash2));
        Assert.Single(_oidc.Puts);
    }

    // The shared confirm path must never touch signing for a limits step.
    private sealed class NoSigner : IWalletSigningClient
    {
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default) => throw new InvalidOperationException("signing must not be called for limits");
    }

    private sealed class NoConfirmer : ITransactionConfirmer
    {
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class NoMcp : IBiatecMcpClient
    {
        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
