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
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;

namespace Biatec.Chat.Tests.Signing;

/// <summary>
/// Confirm, sign at Biatec OIDC and submit (T-022). Needs the docker-compose postgres. Biatec OIDC, Biatec MCP,
/// algod and the evidence log are fakes: no network is touched.
/// </summary>
public sealed class ConfirmServiceTests : IAsyncLifetime
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
        public bool RefreshFails;
        public bool Explodes;
        public Task PutAsync(string id, TokenVaultEntry e, CancellationToken ct = default) { Items[id] = e; return Task.CompletedTask; }
        public Task<TokenVaultEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
        public Task<string?> GetAccessTokenAsync(string id, CancellationToken ct = default) =>
            Explodes ? throw new InvalidOperationException("boom") : RefreshFails ? throw new VaultRefreshException("down") : Task.FromResult(Items.GetValueOrDefault(id)?.AccessToken);
    }

    /// <summary>Biatec OIDC: signs what it is handed with the user's account, unless told otherwise.</summary>
    private sealed class FakeOidc(Account account) : IWalletSigningClient
    {
        public readonly List<(string Bearer, WalletSignRequest Request)> Calls = [];
        public Func<WalletSignRequest, WalletSignOutcome>? Override;
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            Calls.Add((bearer, request));
            if (Override is not null) return Task.FromResult(Override(request));
            var signed = request.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(
                Transaction.FromBase64String(t).Sign(account)))).ToList();
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Signed(signed, "12.00"));
        }
    }

    /// <summary>Biatec MCP: the typed interface has no signing method; only submit is recorded, everything else fails the test.</summary>
    private sealed class FakeMcp : IBiatecMcpClient
    {
        public readonly List<(McpCallContext Ctx, string Network, string Blob)> Submits = [];
        public bool SubmitFails;
        public bool SubmitThrows;
        public bool SubmitCancels;
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default)
        {
            Submits.Add((ctx, network, signedTransactionBase64));
            if (SubmitCancels) throw new OperationCanceledException("request aborted");
            if (SubmitThrows) throw new TimeoutException("submit timed out");
            return Task.FromResult(new McpToolResult(SubmitFails, "", null));
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
        public TxConfirmation Result = new(TxConfirmationState.Confirmed, 4242);
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) { LastValidSeen = lastValid; return Task.FromResult(Result); }
        public ulong LastValidSeen;
    }

    private sealed class FakeEvidence : IEvidenceLog
    {
        public readonly List<(string Kind, JsonObject Payload)> Entries = [];
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default)
        {
            Entries.Add((kind, payload));
            return Task.FromResult(new EvidenceEntry());
        }
        public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly Account _user = new();
    private readonly Account _other = new();
    private readonly MemoryVault _vault = new();
    private readonly FakeMcp _mcp = new();
    private readonly FakeConfirmer _confirmer = new();
    private readonly FakeEvidence _evidence = new();
    private FakeOidc _oidc = null!;
    private Tenant _tenant = null!;
    private ChatSession _session = null!;

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
        GrantScopes("openid", "sign");
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private void GrantScopes(params string[] scopes) =>
        _vault.Items[_session.Id.ToString("N")] = new TokenVaultEntry
        {
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.Now.AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = scopes, AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };

    private static PaymentTransaction Pay(Address sender, ulong amount) => new()
    {
        Sender = sender,
        Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
        Amount = amount, Fee = 1000, FirstValid = 710399, LastValid = 711399,
        GenesisId = "testnet-v1.0",
        GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")),
        Note = Encoding.UTF8.GetBytes("t"),
    };

    private sealed record Built(GroupedTransactions Group, Guid PreviewId);

    /// <summary>Persists a one-step preview whose step holds the grouped unsigned transactions.</summary>
    private async Task<Built> BuildPreviewAsync(Address? sender = null, int expiresInSeconds = 1500, Func<GroupedTransactions, string>? storedGroup = null)
    {
        var group = AlgorandGroup.Ensure([Pay(sender ?? _user.Address, 5_000_000)]);
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var stored = storedGroup?.Invoke(group) ?? JsonSerializer.Serialize(group.ToBase64());
        var snap = await svc.CreateAsync(new NewPreviewSpec(_session.ConversationId, "wallet.transfer", "{\"intent\":\"send\"}",
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.Now.AddSeconds(expiresInSeconds), UnsignedGroup: stored,
                SenderAddress: _user.Address.EncodeAsString(), Network: Network),
        ]));
        return new Built(group, snap.Preview.Id);
    }

    private async Task<ConfirmResult> ConfirmAsync(Guid previewId, string groupHash, IWalletSigningClient? signer = null, IEnumerable<ISettlementHandler>? handlers = null)
    {
        await using var db = NewContext();
        var previews = new ActionPreviewService(db, _clock);
        var svc = new ConfirmService(db, previews, new ScopeGuard(_vault, _clock), _vault, signer ?? _oidc, _mcp, _confirmer, _evidence, _clock,
            NullLogger<ConfirmService>.Instance, handlers);
        return await svc.ConfirmAsync(_tenant, _session, previewId, groupHash, CancellationToken.None);
    }

    private async Task<(ActionPreview Preview, ActionStep Step)> LoadAsync(Guid previewId)
    {
        await using var db = NewContext();
        return (await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == previewId), await db.ActionSteps.AsNoTracking().SingleAsync(s => s.PreviewId == previewId));
    }

    [Fact]
    public async Task HappyPath_SignsTheExactPreviewedBytesAtOidc_SubmitsThemAndRecordsTheReceipt()
    {
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.Equal(b.Group.TxIds, r.TxIds);

        // Biatec OIDC got the previewed unsigned bytes unchanged, the group ID, the vaulted bearer, and one sender.
        var (bearer, req) = Assert.Single(_oidc.Calls);
        Assert.Equal("ACCESS-TOKEN-SECRET", bearer);
        Assert.Equal(b.Group.ToBase64(), req.Transactions);
        Assert.Equal(b.Group.GroupHash, Convert.ToHexStringLower(req.GroupId));
        Assert.Equal(_user.Address.EncodeAsString(), req.Address);
        Assert.Equal(Network, req.Network);
        Assert.Equal("https://chat.biatec.io", req.Origin);
        Assert.InRange(req.MaxConsentSeconds!.Value, 1, 1500);

        // The submitted bytes are the signed transactions OIDC returned, and they embed the previewed group (hash check).
        var (ctx, net, blob) = Assert.Single(_mcp.Submits);
        Assert.Equal(Network, net);
        Assert.Equal(_tenant.Id, ctx.TenantId);
        var signedBytes = Convert.FromBase64String(blob);
        var expected = b.Group.Transactions.SelectMany(t => AlgoEncoder.EncodeToMsgPackOrdered(t.Sign(_user))).ToArray();
        Assert.Equal(expected.Length, signedBytes.Length);
        var decoded = AlgoEncoder.DecodeFromMsgPack<SignedTransaction>(signedBytes);
        Assert.Equal(b.Group.GroupHash, AlgorandGroup.ComputeGroupHash([decoded.Tx]));

        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Confirmed, step.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
        Assert.Equal(ActionPreviewStatus.Completed, preview.Status);
        Assert.Equal(["action_submitted", "action_receipt"], _evidence.Entries.Select(e => e.Kind));
        Assert.Equal("12.00", _evidence.Entries[0].Payload["valuedEur"]!.GetValue<string>());
        Assert.Equal("4242", _evidence.Entries[1].Payload["confirmedRound"]!.GetValue<string>());
        // No token ever reaches evidence.
        Assert.DoesNotContain("SECRET", string.Concat(_evidence.Entries.Select(e => e.Payload.ToJsonString())));
    }

    private const string X402Url = "https://shop.example.com/premium";

    private async Task<Built> BuildX402PreviewAsync()
    {
        var group = AlgorandGroup.Ensure([Pay(_user.Address, 250_000)]);
        await using var db = NewContext();
        var canonical = new JsonObject
        {
            ["capability"] = "x402.pay", ["network"] = "testnet", ["resourceUrl"] = X402Url,
            ["from"] = _user.Address.EncodeAsString(), ["payTo"] = Pay(_user.Address, 1).Receiver.EncodeAsString(),
            ["assetId"] = "10458941", ["amount"] = "250000", ["groupHash"] = group.GroupHash,
        }.ToJsonString();
        var snap = await new ActionPreviewService(db, _clock).CreateAsync(new NewPreviewSpec(_session.ConversationId, "x402.pay", canonical,
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.Now.AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
                SenderAddress: _user.Address.EncodeAsString(), Network: Network),
        ]));
        return new Built(group, snap.Preview.Id);
    }

    private static (Biatec.Chat.Api.X402.X402SettlementHandler Handler, Biatec.Chat.Tests.X402.StubResource Resource, Biatec.Chat.Tools.X402.MockX402Facilitator Facilitator) X402Handler(
        Func<HttpRequestMessage, HttpResponseMessage>? resource = null)
    {
        var options = new Biatec.Chat.Tools.X402.X402Options { Enabled = true, AllowedHosts = ["shop.example.com"] };
        var stub = new Biatec.Chat.Tests.X402.StubResource(resource ?? (r => r.Headers.Contains("X-PAYMENT")
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("premium content") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.PaymentRequired)));
        var facilitator = new Biatec.Chat.Tools.X402.MockX402Facilitator();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient(services, _ => new Biatec.Chat.Tools.X402.X402PayService(
            new Biatec.Chat.Tools.X402.X402HostPolicy(options, new Biatec.Chat.Tests.X402.FakeResolver("93.184.216.34")), new HttpClient(stub), null!, facilitator, options));
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        return (new Biatec.Chat.Api.X402.X402SettlementHandler(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp)), stub, facilitator);
    }

    [Fact]
    public async Task X402_confirm_settles_through_the_facilitator_instead_of_the_mcp_broadcast_and_returns_the_resource()
    {
        var b = await BuildX402PreviewAsync();
        var (handler, resource, facilitator) = X402Handler();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash, handlers: [handler]);

        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.Equal("premium content", r.ResourceContent);
        Assert.Empty(_mcp.Submits);
        var settled = Assert.Single(facilitator.Settled);
        Assert.Single(settled.Payment.SignedGroup);
        Assert.Equal(250000UL, settled.Requirements.MaxAmountRequired);
        Assert.Equal(10458941UL, settled.Requirements.Asset);
        Assert.Equal("algorand-testnet", settled.Requirements.Network);
        Assert.Contains(resource.Seen, q => q.Headers.Contains("X-PAYMENT"));

        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Completed, preview.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
        Assert.Equal(["action_submitted", "action_receipt"], _evidence.Entries.Select(e => e.Kind));
        Assert.DoesNotContain("premium content", string.Concat(_evidence.Entries.Select(e => e.Payload.ToJsonString())));
    }

    [Fact]
    public async Task X402_confirm_fails_cleanly_when_the_facilitator_refuses_to_verify()
    {
        var b = await BuildX402PreviewAsync();
        var (handler, _, facilitator) = X402Handler();
        facilitator.RejectVerify = true;
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash, handlers: [handler]);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Null(r.ResourceContent);
        Assert.Empty(_mcp.Submits);
        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Failed, preview.Status);
        Assert.Empty(step.TxIds);
    }

    [Fact]
    public async Task X402_confirm_keeps_the_step_submitted_when_settlement_is_unknown()
    {
        _confirmer.Result = new TxConfirmation(TxConfirmationState.Pending);
        var b = await BuildX402PreviewAsync();
        var (handler, _, facilitator) = X402Handler();
        facilitator.RejectSettle = true;
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash, handlers: [handler]);

        Assert.Equal(ConfirmOutcome.Submitted, r.Outcome);
        var (_, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Submitted, step.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
    }

    [Fact]
    public async Task X402_confirm_lands_the_step_when_paid_but_the_resource_returns_no_content()
    {
        var b = await BuildX402PreviewAsync();
        var (handler, _, _) = X402Handler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash, handlers: [handler]);

        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.Null(r.ResourceContent);
        Assert.Equal("resource_failed", r.ErrorCode);
        Assert.Equal(ActionPreviewStatus.Completed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task CancelledDuringSubmit_LeavesTheStepSubmittedWithTxIds_SoReconcileCanResolveIt()
    {
        _mcp.SubmitCancels = true;
        var b = await BuildPreviewAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(b.PreviewId, b.Group.GroupHash));

        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Submitted, step.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
        Assert.Equal(ActionPreviewStatus.InProgress, preview.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "submission_unknown");

        // Reconcile finds it on chain.
        await using var db = NewContext();
        var svc = new ConfirmService(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _oidc, _mcp, _confirmer, _evidence, _clock,
            NullLogger<ConfirmService>.Instance);
        var r = await svc.ReconcileAsync(_session, b.PreviewId, CancellationToken.None);
        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.Equal(ActionPreviewStatus.Completed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task NotYetConfirmedOnChain_ReportsSubmitted_AndKeepsTheTxIds()
    {
        _confirmer.Result = new TxConfirmation(TxConfirmationState.Pending);
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Submitted, r.Outcome);
        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Submitted, step.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
        Assert.Equal(ActionPreviewStatus.InProgress, preview.Status);
    }

    [Fact]
    public async Task SignedGroupThatDiffersFromThePreview_IsNeverSubmitted()
    {
        var b = await BuildPreviewAsync();
        // A hostile or buggy signer returns a validly signed but different payment (another amount).
        _oidc.Override = _ =>
        {
            var other = AlgorandGroup.Ensure([Pay(_user.Address, 9_000_000)]);
            return new WalletSignOutcome.Signed(other.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(t.Sign(_user)))).ToList(), null);
        };
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Equal("signed_group_mismatch", r.ErrorCode);
        Assert.Empty(_mcp.Submits);
        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Failed, preview.Status);
        Assert.Equal(StepStatus.Failed, step.Status);
    }

    [Fact]
    public async Task UnsignedReturnedTransactions_AreNeverSubmitted()
    {
        var b = await BuildPreviewAsync();
        _oidc.Override = req => new WalletSignOutcome.Signed(
            req.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(new SignedTransaction { Tx = Transaction.FromBase64String(t) }))).ToList(), null);
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Empty(_mcp.Submits);
    }

    [Fact]
    public async Task StoredGroupThatNoLongerMatchesItsHash_IsNeverSentToOidc()
    {
        var b = await BuildPreviewAsync(storedGroup: _ =>
            JsonSerializer.Serialize(AlgorandGroup.Ensure([Pay(_user.Address, 9_000_000)]).ToBase64()));
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Equal("group_mismatch", r.ErrorCode);
        Assert.Empty(_oidc.Calls);
        Assert.Empty(_mcp.Submits);
    }

    [Fact]
    public async Task GroupWithAnotherSender_IsRefusedBeforeSigning()
    {
        var b = await BuildPreviewAsync(sender: _other.Address); // step says the user's address; the group is from another account
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Empty(_oidc.Calls);
    }

    [Fact]
    public async Task ConfirmedHashThatIsNotThePreviewHash_IsRefused_AndNothingIsSigned()
    {
        var b = await BuildPreviewAsync();
        var ex = await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(b.PreviewId, new string('a', 64)));
        Assert.Equal(PreviewError.HashMismatch, ex.Error);
        Assert.Empty(_oidc.Calls);
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task SessionWithoutSignScope_GetsReauthorize_AndIsNeverSigned()
    {
        GrantScopes("openid");
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.ScopeRequired, r.Outcome);
        Assert.Equal("sign", r.ReauthorizeScope);
        Assert.Empty(_oidc.Calls);
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status); // can confirm again after elevation
    }

    [Fact]
    public async Task StepUpChallenge_StoresAConsentRequest_AndReportsConsentPending()
    {
        var b = await BuildPreviewAsync();
        _oidc.Override = _ => new WalletSignOutcome.StepUp("ar_8f2c", "4821", 300, 2);
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.ConsentPending, r.Outcome);
        Assert.Equal(Biatec.Chat.Api.Contracts.ConsentState.Pending, r.Consent!.State);
        Assert.Equal("4821", r.Consent.BindingCode);
        Assert.Equal(b.PreviewId.ToString(), r.Consent.PreviewId);
        Assert.Equal(_clock.Now.AddSeconds(300), r.Consent.ExpiresAt);
        Assert.Empty(_mcp.Submits);

        await using var db = NewContext();
        var consent = await db.ConsentRequests.SingleAsync();
        Assert.Equal("ar_8f2c", consent.AuthReqId);
        Assert.Equal(b.Group.GroupHash, consent.SubjectHash);
        Assert.Equal(Biatec.Chat.Core.Data.ConsentStatus.Pending, consent.Status);
        Assert.Equal(StepStatus.AwaitingConsent, (await LoadAsync(b.PreviewId)).Step.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "consent_requested");
        // The challenge id and binding code are kept out of evidence payloads.
        Assert.DoesNotContain("ar_8f2c", string.Concat(_evidence.Entries.Select(e => e.Payload.ToJsonString())));
    }

    [Fact]
    public async Task OidcOutage_SaysSignInServiceUnavailable_KeepsSessionAndVault_AndRecordsAnOutageEvent_ThenRetrySucceeds()
    {
        var b = await BuildPreviewAsync();
        _oidc.Override = _ => new WalletSignOutcome.Unavailable("HttpRequestException");
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.SignInUnavailable, r.Outcome);
        Assert.Contains("sign-in service is unavailable", r.Message);
        Assert.Empty(_mcp.Submits);
        Assert.Contains(_evidence.Entries, e => e.Kind == "oidc_outage");

        // Still Authenticated, vault untouched (tokens and scopes), preview awaiting confirmation again.
        await using (var db = NewContext())
            Assert.Equal(SessionTier.Authenticated, (await db.ChatSessions.AsNoTracking().SingleAsync()).Tier);
        Assert.Equal(["openid", "sign"], _vault.Items[_session.Id.ToString("N")].Scopes);
        Assert.Equal("ACCESS-TOKEN-SECRET", _vault.Items[_session.Id.ToString("N")].AccessToken);
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);

        _oidc.Override = null;
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
    }

    [Fact]
    public async Task TokenRefreshOutage_IsTreatedAsSignInServiceUnavailable()
    {
        _vault.RefreshFails = true;
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.SignInUnavailable, r.Outcome);
        Assert.Empty(_oidc.Calls);
        Assert.Single(_vault.Items);
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task OidcRefusal_FailsThePreview_WithoutSubmitting()
    {
        var b = await BuildPreviewAsync();
        _oidc.Override = _ => new WalletSignOutcome.Rejected(403, "daily_cap_exceeded");
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Contains("spending limit", r.Message);
        Assert.Empty(_mcp.Submits);
        Assert.Equal(ActionPreviewStatus.Failed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task SubmitFailure_FailsThePreview_AndRecordsIt()
    {
        _mcp.SubmitFails = true;
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Failed, r.Outcome);
        Assert.Equal("submit_failed", r.ErrorCode);
        Assert.Contains(_evidence.Entries, e => e.Kind == "submission_failed");
        Assert.Equal(ActionPreviewStatus.Failed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task SubmitThatThrows_IsUnknown_StepStaysSubmittedWithTxIds_AndIsTrackedOnChain()
    {
        _mcp.SubmitThrows = true;
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        // The group did land even though the submit call timed out.
        Assert.Equal(ConfirmOutcome.Landed, r.Outcome);
        Assert.Contains(_evidence.Entries, e => e.Kind == "submission_unknown");
        Assert.DoesNotContain(_evidence.Entries, e => e.Kind == "submission_failed");
        Assert.Equal(ActionPreviewStatus.Completed, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task SubmitThatThrows_AndNotSeenOnChain_StaysSubmitted_NeverFailed_ThenReconcileLandsIt()
    {
        _mcp.SubmitThrows = true;
        _confirmer.Result = new TxConfirmation(TxConfirmationState.Pending);
        var b = await BuildPreviewAsync();
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.Submitted, r.Outcome);
        Assert.DoesNotContain("Nothing moved", r.Message);
        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Submitted, step.Status);
        Assert.Equal(b.Group.TxIds, step.TxIds);
        Assert.Equal(ActionPreviewStatus.InProgress, preview.Status);

        _confirmer.Result = new TxConfirmation(TxConfirmationState.Confirmed, 777);
        await using (var db = NewContext())
        {
            var svc = new ConfirmService(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _oidc, _mcp, _confirmer, _evidence, _clock, NullLogger<ConfirmService>.Instance);
            var rec = await svc.ReconcileAsync(_session, b.PreviewId, CancellationToken.None);
            Assert.Equal(ConfirmOutcome.Landed, rec.Outcome);
        }
        (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Confirmed, step.Status);
        Assert.Equal(ActionPreviewStatus.Completed, preview.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "action_receipt" && e.Payload["confirmedRound"]!.GetValue<string>() == "777");
    }

    [Fact]
    public async Task SubmittedStepThatNeverLands_ExpiresOnReconcile_AndFailsThePreviewSoItCanBeRebuilt()
    {
        _mcp.SubmitThrows = true;
        _confirmer.Result = new TxConfirmation(TxConfirmationState.Pending);
        var b = await BuildPreviewAsync();
        Assert.Equal(ConfirmOutcome.Submitted, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
        Assert.Equal(711399UL, _confirmer.LastValidSeen);

        _confirmer.Result = new TxConfirmation(TxConfirmationState.Expired, Error: "last_valid_passed");
        await using (var db = NewContext())
        {
            var svc = new ConfirmService(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _oidc, _mcp, _confirmer, _evidence, _clock, NullLogger<ConfirmService>.Instance);
            var rec = await svc.ReconcileAsync(_session, b.PreviewId, CancellationToken.None);
            Assert.Equal(ConfirmOutcome.Failed, rec.Outcome);
            Assert.Equal("expired", rec.ErrorCode);
        }
        var (preview, step) = await LoadAsync(b.PreviewId);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Equal(ActionPreviewStatus.Failed, preview.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "submission_failed" && e.Payload["reason"]!.GetValue<string>() == "expired");
    }

    [Fact]
    public async Task UnexpectedErrorAfterConfirm_RevertsThePreview_SoItCanBeConfirmedAgain()
    {
        var b = await BuildPreviewAsync();
        _vault.Explodes = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(b.PreviewId, b.Group.GroupHash));
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "confirm_aborted");

        _vault.Explodes = false;
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
    }

    [Fact]
    public async Task AbortedRequestDuringSigning_RevertsThePreview_AndRecordsTheAbort()
    {
        var b = await BuildPreviewAsync();
        using var cts = new CancellationTokenSource();
        var aborting = new FakeOidc(_user) { Override = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); } };
        await using (var db = NewContext())
        {
            var svc = new ConfirmService(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, aborting, _mcp, _confirmer, _evidence, _clock, NullLogger<ConfirmService>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ConfirmAsync(_tenant, _session, b.PreviewId, b.Group.GroupHash, cts.Token));
        }
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "confirm_aborted" && e.Payload["reason"]!.GetValue<string>() == "cancelled");
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
    }

    [Fact]
    public async Task InvalidToken_RevertsThePreview_SoItCanBeConfirmedAfterReauth()
    {
        var b = await BuildPreviewAsync();
        _oidc.Override = _ => new WalletSignOutcome.Rejected(401, "invalid_token");
        var r = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        Assert.Equal(ConfirmOutcome.SignInRequired, r.Outcome);
        Assert.Equal(ActionPreviewStatus.Proposed, (await LoadAsync(b.PreviewId)).Preview.Status);

        _oidc.Override = null;
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
    }

    [Fact]
    public async Task ConcurrentConfirms_OfTheSameGroup_SignAndSubmitOnce()
    {
        var b = await BuildPreviewAsync();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(async _ =>
            {
                try { return (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome.ToString(); }
                catch (PreviewException) { return "refused"; }
            }));

        Assert.Equal(1, results.Count(x => x == "Landed"));
        Assert.Single(_oidc.Calls);
        Assert.Single(_mcp.Submits);
    }

    [Fact]
    public async Task SecondConfirm_OfTheSameGroup_IsRefused_NoDoubleSigning()
    {
        var b = await BuildPreviewAsync();
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(b.PreviewId, b.Group.GroupHash)).Outcome);
        await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(b.PreviewId, b.Group.GroupHash));
        Assert.Single(_oidc.Calls);
        Assert.Single(_mcp.Submits);
    }

    [Fact]
    public async Task ExpiredOrTooCloseToExpiry_IsRefused_BeforeSigning()
    {
        var b = await BuildPreviewAsync(expiresInSeconds: 300); // under the 600 second Confirm gate
        var ex = await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(b.PreviewId, b.Group.GroupHash));
        Assert.Equal(PreviewError.TooCloseToExpiry, ex.Error);
        Assert.Empty(_oidc.Calls);
    }

    [Fact]
    public async Task MultiStepAction_CompletesOnlyOnTheFinalStep()
    {
        var group = AlgorandGroup.Ensure([Pay(_user.Address, 1_000_000)]);
        Guid previewId;
        await using (var db = NewContext())
        {
            var snap = await new ActionPreviewService(db, _clock).CreateAsync(new NewPreviewSpec(_session.ConversationId, "wallet.transfer",
                "{\"totalSteps\":2}",
                [new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.Now.AddSeconds(1500), JsonSerializer.Serialize(group.ToBase64()),
                    SenderAddress: _user.Address.EncodeAsString(), Network: Network)]));
            previewId = snap.Preview.Id;
        }
        Assert.Equal(ConfirmOutcome.Landed, (await ConfirmAsync(previewId, group.GroupHash)).Outcome);
        Assert.Equal(ActionPreviewStatus.InProgress, (await LoadAsync(previewId)).Preview.Status);
    }

    // ---- the HTTP client that talks to Biatec OIDC ----

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request);
        }
    }

    private static WalletSigningClient ClientFor(StubHandler h) => new(new HttpClient(h) { BaseAddress = new Uri("https://oidc.example.test/") });

    private static HttpResponseMessage Json(int status, string json) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static WalletSignRequest SampleRequest() => new(Network, "ADDR", ["AAAA"], new byte[32], MaxConsentSeconds: 900, Origin: "https://chat.biatec.io");

    [Fact]
    public async Task WalletSigningClient_PostsToTheOidcWalletSignEndpoint_NeverToMcp()
    {
        var h = new StubHandler(_ => Task.FromResult(Json(200, "{\"signedTransactions\":[\"QQ==\"],\"valuedEur\":\"10000.00\"}")));
        var r = await ClientFor(h).SignAsync("BEARER", SampleRequest());

        var signed = Assert.IsType<WalletSignOutcome.Signed>(r);
        Assert.Equal(["QQ=="], signed.SignedTransactions);
        Assert.Equal("10000.00", signed.ValuedEur);
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        Assert.Equal("https://oidc.example.test/wallet/algorand-testnet/ADDR/sign", h.Last.RequestUri!.ToString());
        Assert.Equal("Bearer BEARER", h.Last.Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(h.LastBody!).RootElement;
        Assert.Equal("AAAA", body.GetProperty("transactions")[0].GetString());
        Assert.Equal(Convert.ToBase64String(new byte[32]), body.GetProperty("grp").GetString());
        Assert.False(body.GetProperty("requireConsent").GetBoolean());
        Assert.Equal(900, body.GetProperty("maxConsentSeconds").GetInt32());
        Assert.Equal("https://chat.biatec.io", body.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task WalletSigningClient_ParsesTheStepUpChallenge()
    {
        var h = new StubHandler(_ => Task.FromResult(Json(401,
            "{\"error\":\"insufficient_user_authentication\",\"error_description\":\"consent required\",\"acr_values\":\"biatec:consent\",\"auth_req_id\":\"ar_8f2c\",\"binding_code\":\"4821\",\"expires_in\":300,\"interval\":2}")));
        var r = Assert.IsType<WalletSignOutcome.StepUp>(await ClientFor(h).SignAsync("B", SampleRequest()));
        Assert.Equal(("ar_8f2c", "4821", 300, 2), (r.AuthReqId, r.BindingCode, r.ExpiresInSeconds, r.IntervalSeconds));
    }

    [Fact]
    public async Task WalletSigningClient_StepUpWithoutAnAuthReqId_IsARefusal()
    {
        var h = new StubHandler(_ => Task.FromResult(Json(401, "{\"error\":\"insufficient_user_authentication\"}")));
        Assert.IsType<WalletSignOutcome.Rejected>(await ClientFor(h).SignAsync("B", SampleRequest()));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(429)]
    public async Task WalletSigningClient_ServerFailures_AreAnOutage(int status)
    {
        var h = new StubHandler(_ => Task.FromResult(Json(status, "{}")));
        Assert.IsType<WalletSignOutcome.Unavailable>(await ClientFor(h).SignAsync("B", SampleRequest()));
    }

    [Fact]
    public async Task WalletSigningClient_NetworkFailureAndTimeout_AreAnOutage()
    {
        Assert.IsType<WalletSignOutcome.Unavailable>(await ClientFor(new StubHandler(_ => throw new HttpRequestException("down"))).SignAsync("B", SampleRequest()));
        Assert.IsType<WalletSignOutcome.Unavailable>(await ClientFor(new StubHandler(_ => throw new TaskCanceledException("timeout"))).SignAsync("B", SampleRequest()));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    public async Task WalletSigningClient_ClientErrors_AreRefusals_NotOutages(int status)
    {
        var h = new StubHandler(_ => Task.FromResult(Json(status, "{\"error\":\"daily_cap_exceeded\"}")));
        var r = Assert.IsType<WalletSignOutcome.Rejected>(await ClientFor(h).SignAsync("B", SampleRequest()));
        Assert.Equal(status, r.Status);
        Assert.Equal("daily_cap_exceeded", r.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"signedTransactions\":[]}")]
    [InlineData("{\"signedTransactions\":[1]}")]
    public async Task WalletSigningClient_MalformedSuccess_IsRefused(string body)
    {
        var h = new StubHandler(_ => Task.FromResult(Json(200, body)));
        Assert.IsType<WalletSignOutcome.Rejected>(await ClientFor(h).SignAsync("B", SampleRequest()));
    }

    [Fact]
    public void MarkingSignTransactionAsBlocked_StillHolds_ForTheMcpClient()
    {
        // Signing is never delegated to Biatec MCP: the tool is blocked by name and the typed client has no method for it.
        Assert.True(McpToolNames.IsBlocked(McpToolNames.SignTransaction));
        Assert.DoesNotContain(typeof(IBiatecMcpClient).GetMethods(), m => m.Name.Contains("Sign", StringComparison.OrdinalIgnoreCase));
    }
}
