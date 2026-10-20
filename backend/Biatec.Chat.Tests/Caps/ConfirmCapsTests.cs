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
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;

namespace Biatec.Chat.Tests.Caps;

/// <summary>
/// The soft cap and safe caps in the confirm path (T-025): reserve before signing, requireConsent over the cap, release
/// on step-up, refusal, outage and failure, finalize on signature. Needs the docker-compose postgres; everything else is fake.
/// </summary>
public sealed class ConfirmCapsTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";
    private const string Network = "algorand-testnet";

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
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default) =>
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
            NullLogger<ConfirmService>.Instance, recorder: _recorder, caps: new SpendCaps(db, _clock, _capsOptions));

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

    [Fact]
    public async Task Caps_a_signed_action_is_reserved_then_finalized_with_the_oidc_value()
    {
        var r = await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.False(Assert.Single(_oidc.Calls).RequireConsent);
        var row = Assert.Single(await SpendsAsync());
        Assert.Equal((SoftCapStatus.Final, 12.00m, "oidc", "user-1", _tenant.Id), (row.Status, row.AmountEur, row.ValueSource, row.OidcSub, row.TenantId));
    }

    [Fact]
    public async Task Caps_require_consent_is_set_on_the_sign_request_while_the_window_total_exceeds_the_cap()
    {
        _oidc.ValuedEur = "1400";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(1400m))).Outcome);

        // 1400 + 200 > 1500: Biatec OIDC is asked to open the consent request, and nothing is counted.
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2);
        Assert.Equal(ConfirmOutcome.ConsentPending, (await ConfirmAsync(await BuildPreviewAsync(200m))).Outcome);
        Assert.True(_oidc.Calls[^1].RequireConsent);
        Assert.Equal(1400m, (await SpendsAsync()).Sum(x => x.AmountEur));

        // A small action that still fits does not need consent: the check is per action, not sticky.
        _oidc.Override = null;
        _oidc.ValuedEur = "50";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(50m))).Outcome);
        Assert.False(_oidc.Calls[^1].RequireConsent);

        // An hour later the window has rolled.
        _clock.Now = _clock.Now.AddMinutes(61);
        _oidc.ValuedEur = "200";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(200m))).Outcome);
        Assert.False(_oidc.Calls[^1].RequireConsent);
    }

    [Theory]
    [InlineData("stepup")]
    [InlineData("rejected")]
    [InlineData("unavailable")]
    [InlineData("throws")]
    public async Task Caps_release_the_reservation_when_oidc_does_not_sign(string kind)
    {
        _oidc.Override = kind switch
        {
            "stepup" => _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2),
            "rejected" => _ => new WalletSignOutcome.Rejected(403, "daily_cap_exceeded"),
            "unavailable" => _ => new WalletSignOutcome.Unavailable("HttpRequestException"),
            _ => _ => throw new InvalidOperationException("boom"),
        };
        var b = await BuildPreviewAsync(1500m);
        if (kind == "throws") await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(b));
        else await ConfirmAsync(b);
        Assert.False(Assert.Single(_oidc.Calls).RequireConsent);
        Assert.Empty(await SpendsAsync());

        // The released 1500 EUR is available again.
        _oidc.Override = null;
        _oidc.ValuedEur = "1500";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(1500m))).Outcome);
        Assert.False(_oidc.Calls[^1].RequireConsent);
    }

    [Fact]
    public async Task Caps_an_action_signed_after_consent_is_never_counted()
    {
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2);
        var b = await BuildPreviewAsync(1000m);
        Assert.Equal(ConfirmOutcome.ConsentPending, (await ConfirmAsync(b)).Outcome);
        _oidc.Override = null;
        await using (var db = NewContext())
            Assert.Equal(ConfirmOutcome.Landed, (await NewService(db).ResumeAfterConsentAsync(_tenant, _session, b.PreviewId, "ELEVATED", CancellationToken.None)).Outcome);
        Assert.Empty(await SpendsAsync());
    }

    [Fact]
    public async Task Caps_safe_approvals_over_the_cap_require_consent_and_are_not_in_the_soft_cap()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(10m, SpendCaps.SafeApprove))).Outcome);
            Assert.False(_oidc.Calls[^1].RequireConsent);
        }
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2);
        Assert.Equal(ConfirmOutcome.ConsentPending, (await ConfirmAsync(await BuildPreviewAsync(10m, SpendCaps.SafeApprove))).Outcome);
        Assert.True(_oidc.Calls[^1].RequireConsent);

        Assert.Empty(await SpendsAsync());
        await using var db = NewContext();
        Assert.Equal(5, await db.SafeApprovalCounts.CountAsync(x => x.Kind == SafeApprovalKind.Approve));
    }

    [Fact]
    public async Task Caps_safe_execution_cap_is_separate_from_approvals()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(999m, SpendCaps.SafeExecute))).Outcome);
        Assert.All(_oidc.Calls, c => Assert.False(c.RequireConsent));
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2);
        await ConfirmAsync(await BuildPreviewAsync(0m, SpendCaps.SafeExecute));
        Assert.True(_oidc.Calls[^1].RequireConsent);

        _oidc.Override = null;
        await ConfirmAsync(await BuildPreviewAsync(0m, SpendCaps.SafeApprove));
        Assert.False(_oidc.Calls[^1].RequireConsent);
        await using var db = NewContext();
        // Executions are valued at zero.
        Assert.Equal(0m, await db.SafeApprovalCounts.Where(x => x.Kind == SafeApprovalKind.Execute).SumAsync(x => x.ValueEur));
    }

    private async Task FillWindowAsync()
    {
        _oidc.ValuedEur = "1500";
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(1500m))).Outcome);
        _oidc.ValuedEur = "12.00";
    }

    [Fact]
    public async Task Caps_consent_forced_by_the_cap_tells_the_user_why_and_is_recorded_in_signing_requested()
    {
        await FillWindowAsync();
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar", "12", 300, 2);
        var r = await ConfirmAsync(await BuildPreviewAsync(10m));
        Assert.Equal(ConfirmOutcome.ConsentPending, r.Outcome);
        Assert.Contains("hourly spending limit", r.Message);
        var requested = _recorder.Entries.Where(e => e.Kind == EvidenceKinds.SigningRequested).Select(e => e.Payload).ToList();
        Assert.Equal(2, requested.Count);
        Assert.False(requested[0]["requireConsent"]!.GetValue<bool>());
        Assert.Null(requested[0]["capReason"]);
        Assert.True(requested[1]["requireConsent"]!.GetValue<bool>());
        Assert.Equal("soft_cap", requested[1]["capReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Caps_fail_closed_when_consent_was_required_and_oidc_signed_without_a_challenge()
    {
        await FillWindowAsync();
        var submits = _mcp.Submits;
        var r = await ConfirmAsync(await BuildPreviewAsync(10m)); // the fake signs regardless of requireConsent
        Assert.Equal((ConfirmOutcome.Failed, "consent_not_enforced"), (r.Outcome, r.ErrorCode));
        Assert.Contains("approval on your phone", r.Message);
        Assert.True(_oidc.Calls[^1].RequireConsent);
        Assert.Equal(submits, _mcp.Submits);
        Assert.Equal(1500m, (await SpendsAsync()).Sum(x => x.AmountEur));
    }

    [Fact]
    public async Task Caps_the_previews_own_require_consent_flag_is_ored_in_and_also_fails_closed()
    {
        var r = await ConfirmAsync(await BuildPreviewAsync(10m, canonical: "{\"intent\":\"send\",\"requireConsent\":true}"));
        Assert.True(Assert.Single(_oidc.Calls).RequireConsent);
        Assert.Equal("consent_not_enforced", r.ErrorCode);
        Assert.Equal(0, _mcp.Submits);
        Assert.Empty(await SpendsAsync()); // its reservation was released
    }

    [Fact]
    public async Task Caps_are_a_hard_stop_when_oidc_does_not_honour_require_consent()
    {
        await FillWindowAsync();
        _capsOptions.OidcHonoursRequireConsent = false;
        var calls = _oidc.Calls.Count;
        var b = await BuildPreviewAsync(10m);
        var r = await ConfirmAsync(b);
        Assert.Equal((ConfirmOutcome.Failed, "soft_cap"), (r.Outcome, r.ErrorCode));
        Assert.Contains("hourly spending limit", r.Message);
        Assert.Equal(calls, _oidc.Calls.Count); // never sent to Biatec OIDC
        await using (var db = NewContext())
            Assert.Equal(ActionPreviewStatus.Proposed, (await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == b.PreviewId)).Status);

        // Under the cap nothing changes.
        _clock.Now = _clock.Now.AddMinutes(61);
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(await BuildPreviewAsync(10m))).Outcome);
    }

    [Theory]
    [InlineData("mcp_error", "wallet.transfer")]
    [InlineData("network_rejected", "wallet.transfer")]
    [InlineData("expired", "wallet.transfer")]
    [InlineData("mcp_error", SpendCaps.SafeApprove)]
    public async Task Caps_a_signed_action_that_moved_nothing_is_not_counted(string kind, string capability)
    {
        if (kind == "mcp_error") _mcp.Fails = true;
        else _confirmer.State = kind == "expired" ? TxConfirmationState.Expired : TxConfirmationState.Rejected;
        Assert.Equal(ConfirmOutcome.Failed, (await ConfirmAsync(await BuildPreviewAsync(10m, capability))).Outcome);
        Assert.Empty(await SpendsAsync());
        await using var db = NewContext();
        Assert.Empty(await db.SafeApprovalCounts.ToListAsync());
    }
}
