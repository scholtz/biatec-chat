using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Caps;
using Biatec.Chat.Tools.Caps.NoDevice;
using Biatec.Chat.Tools.Consent;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;

namespace Biatec.Chat.Tests.Caps.NoDevice;

/// <summary>
/// T-061: the backend fallback for rule 4 / rule 8 (required-changes row 10) in the confirm path, with fake consent
/// settings. Needs the docker-compose postgres; everything else is fake.
/// </summary>
public sealed class NoDeviceCapTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";
    private const string Network = "algorand-testnet";

    /// <summary>Fake Biatec OIDC <c>GET /consent/settings</c>: no active device by default.</summary>
    private sealed class FakeSettings : IConsentSettingsClient
    {
        public ConsentSettings? Value = new(250m, DeviceEnrolled: false, DeviceActive: false, AlwaysAsk: false, CoolingOff: false, FirstUse: false);
        public int Calls;
        public bool Missing; // the endpoint answers 404
        public Task<ConsentSettings?> GetAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentSettingsFetch> FetchAsync(Uri settingsEndpoint, string bearer, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal("ACCESS", bearer);
            return Task.FromResult(Missing ? new ConsentSettingsFetch(null, true) : new ConsentSettingsFetch(Value, false));
        }
    }

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

    private sealed class FakeOidc(Account account) : IWalletSigningClient
    {
        public readonly List<WalletSignRequest> Calls = [];
        public Func<WalletSignRequest, WalletSignOutcome>? Override;
        public string? ValuedEur = "12.00";
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            Calls.Add(request);
            if (Override is not null) return Task.FromResult(Override(request));
            var signed = request.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(
                Transaction.FromBase64String(t).Sign(account)))).ToList();
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Signed(signed, ValuedEur));
        }
    }

    private sealed class FakeMcp : IBiatecMcpClient
    {
        public int Submits;
        public bool Fails;
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default)
        {
            Submits++;
            return Task.FromResult(new McpToolResult(Fails, "", null));
        }
        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfirmer : ITransactionConfirmer
    {
        public TxConfirmationState State = TxConfirmationState.Confirmed;
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) =>
            Task.FromResult(new TxConfirmation(State, 4242));
    }

    private sealed class FakeRecorder : IEvidenceRecorder
    {
        public readonly List<(string Kind, JsonObject Payload)> Entries = [];
        public Task RecordAsync(Guid conversationId, string kind, JsonObject payload, CancellationToken ct = default)
        {
            Entries.Add((kind, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEvidence : IEvidenceLog
    {
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, string? anchorRef = null, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceEntry());
        public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly Account _user = new();
    private readonly MemoryVault _vault = new();
    private FakeOidc _oidc = null!;
    private Tenant _tenant = null!;
    private ChatSession _session = null!;
    private ulong _amount = 1_000_000;
    private readonly FakeMcp _mcp = new();
    private readonly FakeConfirmer _confirmer = new();
    private readonly FakeRecorder _recorder = new();
    private readonly CapsOptions _capsOptions = new() { OidcHonoursRequireConsent = true };
    private readonly FakeSettings _settings = new();
    private readonly NoDeviceCapOptions _noDeviceOptions = new();
    private NoDeviceSettingsCache _cache = null!;
    private bool _withoutCaps;

    private ChatDbContext NewContext()
    {
        var cs = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        _oidc = new FakeOidc(_user);
        _cache = new NoDeviceSettingsCache(_clock);
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
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
        _vault.Items[_session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS", AccessExpiresAt = _clock.Now.AddMinutes(10), RefreshToken = "REFRESH",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private sealed record Built(GroupedTransactions Group, Guid PreviewId);

    private async Task<Built> BuildPreviewAsync(decimal? valueEur, string capability = "wallet.transfer", string canonical = "{\"intent\":\"send\"}")
    {
        // A distinct amount per preview gives each group its own hash.
        var group = AlgorandGroup.Ensure([new PaymentTransaction
        {
            Sender = _user.Address, Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
            Amount = _amount++, Fee = 1000, FirstValid = 710399, LastValid = 711399, GenesisId = "testnet-v1.0",
            GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")), Note = Encoding.UTF8.GetBytes("t"),
        }]);
        await using var db = NewContext();
        var snap = await new ActionPreviewService(db, _clock).CreateAsync(new NewPreviewSpec(_session.ConversationId, capability, canonical,
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.Now.AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
                SenderAddress: _user.Address.EncodeAsString(), Network: Network),
        ], PredictedValueEur: valueEur));
        return new Built(group, snap.Preview.Id);
    }

    private ConfirmService NewService(ChatDbContext db) =>
        new(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _oidc, _mcp, _confirmer, new FakeEvidence(), _clock,
            NullLogger<ConfirmService>.Instance, recorder: _recorder, caps: _withoutCaps ? null : new SpendCaps(db, _clock, _capsOptions, _noDeviceOptions),
            noDevice: new NoDeviceGate(_settings, () => new Uri("https://oidc.test/consent/settings"), _noDeviceOptions, _cache));

    private async Task<ConfirmResult> ConfirmAsync(Built b)
    {
        await using var db = NewContext();
        return await NewService(db).ConfirmAsync(_tenant, _session, b.PreviewId, b.Group.GroupHash, Biatec.Chat.Api.Chat.Confirm.ConfirmService.BackendOrigin(_tenant), CancellationToken.None);
    }

    private async Task<List<SoftCapSpend>> SpendsAsync()
    {
        await using var db = NewContext();
        return await db.SoftCapSpends.AsNoTracking().ToListAsync();
    }

    private async Task<ActionPreviewStatus> StatusAsync(Guid previewId)
    {
        await using var db = NewContext();
        return (await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == previewId)).Status;
    }

    [Fact]
    public async Task NoDevice_user_is_refused_once_the_days_total_would_exceed_the_cap()
    {
        // Earlier spend: 500 EUR just over a day ago (rolled out of the window), 60 EUR five hours ago (past the
        // soft cap's hour, still inside the day). The session's tokens stay valid because the clock is put back.
        var now = _clock.Now;
        foreach (var (ago, eur) in new[] { (TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)), 500m), (TimeSpan.FromHours(5), 60m) })
        {
            _clock.Now = now - ago;
            var step = Guid.NewGuid();
            await using var db = NewContext();
            var caps = new SpendCaps(db, _clock, _capsOptions, _noDeviceOptions);
            await caps.ReserveAsync(new CapRequest(_tenant, "user-1", step, "wallet.transfer", eur));
            await caps.FinalizeAsync(step, null);
        }
        _clock.Now = now;
        await using (var db = NewContext()) await new SpendCaps(db, _clock, _capsOptions, _noDeviceOptions).PruneAsync();
        Assert.Equal(2, (await SpendsAsync()).Count); // the pruner keeps a day plus one hour of final rows

        _oidc.ValuedEur = null; // the backend's prediction is counted
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(40m))).Outcome); // exactly 100 today
        var calls = _oidc.Calls.Count;

        var b = await BuildPreviewAsync(1m);
        var r = await ConfirmAsync(b);
        Assert.Equal((ConfirmOutcome.Failed, NoDevicePolicy.DailyCapReason), (r.Outcome, r.ErrorCode));
        Assert.Contains("daily limit", r.Message);
        Assert.Contains("Enroll the Biatec authenticator", r.Message);
        Assert.Equal(calls, _oidc.Calls.Count); // never sent to Biatec OIDC
        Assert.Equal(ActionPreviewStatus.Proposed, await StatusAsync(b.PreviewId));
        Assert.Equal(600m, (await SpendsAsync()).Sum(x => x.AmountEur));

        // A safe execution (valued at zero) still fits.
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(5000m, SpendCaps.SafeExecute))).Outcome);
    }

    [Fact]
    public async Task NoDevice_safe_executions_add_nothing_to_the_reduced_cap()
    {
        _oidc.ValuedEur = "100";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(100m))).Outcome);
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(5000m, SpendCaps.SafeExecute))).Outcome);
    }

    [Theory]
    [InlineData("above")]
    [InlineData("unknown_value")]
    [InlineData("always_ask")]
    [InlineData("preview_flag")]
    [InlineData("inactive_device")]
    [InlineData("cooling_off")]
    [InlineData("first_use")]
    [InlineData("threshold_above_ceiling")]
    public async Task NoDevice_above_threshold_action_is_refused_with_an_enrollment_hint(string kind)
    {
        decimal? value = kind switch { "above" or "inactive_device" => 300m, "unknown_value" => null, "threshold_above_ceiling" => 1200m, _ => 10m };
        if (kind == "always_ask") _settings.Value = _settings.Value! with { AlwaysAsk = true };
        if (kind == "cooling_off") _settings.Value = _settings.Value! with { CoolingOff = true };
        if (kind == "first_use") _settings.Value = _settings.Value! with { FirstUse = true };
        if (kind == "threshold_above_ceiling") { _settings.Value = _settings.Value! with { ThresholdEur = 5000m }; _noDeviceOptions.DailyCapEur = 5000m; } // clamped to 1000
        if (kind == "inactive_device") _settings.Value = _settings.Value! with { DeviceEnrolled = true }; // enrolled, still in its 24 h delay
        var b = await BuildPreviewAsync(value, canonical: kind == "preview_flag" ? "{\"intent\":\"send\",\"requireConsent\":true}" : "{\"intent\":\"send\"}");

        var r = await ConfirmAsync(b);
        Assert.Equal((ConfirmOutcome.Failed, NoDevicePolicy.NeedsDeviceReason), (r.Outcome, r.ErrorCode));
        Assert.Contains("approval on your phone", r.Message);
        Assert.Contains("Enroll the Biatec authenticator", r.Message);
        Assert.Empty(_oidc.Calls);
        Assert.Empty(await SpendsAsync());
        Assert.Equal(ActionPreviewStatus.Proposed, await StatusAsync(b.PreviewId));
    }

    [Fact]
    public async Task NoDevice_a_cap_forcing_consent_is_refused_instead_of_sent_to_a_missing_phone()
    {
        _noDeviceOptions.DailyCapEur = 5000m; // the soft cap trips first
        _settings.Value = _settings.Value! with { ThresholdEur = 1000m };
        _oidc.ValuedEur = "900";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(900m))).Outcome);
        _oidc.ValuedEur = "500";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(500m))).Outcome);
        var r = await ConfirmAsync(await BuildPreviewAsync(500m)); // 1400 + 500 > 1500
        Assert.Equal((ConfirmOutcome.Failed, NoDevicePolicy.NeedsDeviceReason), (r.Outcome, r.ErrorCode));
        Assert.Contains("Enroll the Biatec authenticator", r.Message);
        Assert.Equal(2, _oidc.Calls.Count);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("unknown")]
    [InlineData("switched_off")]
    public async Task NoDevice_users_with_an_active_device_or_unknown_status_are_unaffected(string kind)
    {
        if (kind == "active") _settings.Value = _settings.Value! with { DeviceEnrolled = true, DeviceActive = true };
        if (kind == "unknown") _settings.Value = null; // GET /consent/settings missing or failing: OIDC's step-up is relied on
        if (kind == "switched_off") _noDeviceOptions.Enabled = false; // Biatec OIDC enforces rule 4 itself
        _oidc.ValuedEur = "200";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(200m))).Outcome);
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(200m))).Outcome);
        Assert.Equal(2, _oidc.Calls.Count);
        Assert.All(_oidc.Calls, c => Assert.False(c.RequireConsent));
        // An answer is cached for the session; a failure is not.
        Assert.Equal(kind switch { "switched_off" => 0, "active" => 1, _ => 2 }, _settings.Calls);
    }

    [Fact]
    public async Task NoDevice_without_a_cap_store_is_refused_saying_the_limit_could_not_be_checked()
    {
        _withoutCaps = true;
        var r = await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal((ConfirmOutcome.Failed, NoDevicePolicy.UncheckedReason), (r.Outcome, r.ErrorCode));
        Assert.Contains("couldn't be checked", r.Message);
        Assert.DoesNotContain("You've reached", r.Message);
        Assert.Empty(_oidc.Calls);
    }

    [Fact]
    public async Task NoDevice_settings_are_cached_per_session_and_a_missing_endpoint_is_remembered()
    {
        _settings.Value = _settings.Value! with { DeviceEnrolled = true, DeviceActive = true };
        _oidc.ValuedEur = "10";
        await ConfirmAsync(await BuildPreviewAsync(10m));
        await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal(1, _settings.Calls);
        _clock.Now = _clock.Now.AddSeconds(61);
        await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal(2, _settings.Calls);

        // 404: not asked again for a while, and the user is not affected (OIDC's step-up is relied on).
        _clock.Now = _clock.Now.AddSeconds(61);
        _settings.Missing = true;
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(10m))).Outcome);
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(10m))).Outcome);
        Assert.Equal(3, _settings.Calls);
        _clock.Now = _clock.Now.AddMinutes(11);
        _vault.Items[_session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS", AccessExpiresAt = _clock.Now.AddMinutes(10), RefreshToken = "REFRESH",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };
        await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal(4, _settings.Calls);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "not a url")]
    public async Task NoDevice_registration_resolves_without_settings_client_or_issuer(bool enabled, string issuer)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caps:NoDevice:Enabled"] = enabled ? "true" : "false",
        }).Build());
        services.AddSingleton<TimeProvider>(_clock);
        services.Configure<Biatec.Chat.Api.Config.OidcOptions>(o => o.Issuer = issuer);
        if (enabled) services.AddSingleton<IConsentSettingsClient>(_settings);
        Biatec.Chat.Api.Caps.CapsRegistration.AddSpendCaps(services);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<INoDeviceGate>();
        Assert.Equal(enabled, gate is NoDeviceGate);
        Assert.Null(await gate.CheckAsync("s", "ACCESS"));
        Assert.Equal(0, _settings.Calls);
    }
}
