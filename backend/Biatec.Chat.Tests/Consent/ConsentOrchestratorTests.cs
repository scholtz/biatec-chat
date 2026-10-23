using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using ActionPreview = Biatec.Chat.Core.Data.ActionPreview;
using ActionStep = Biatec.Chat.Core.Data.ActionStep;
using ActionPreviewStatus = Biatec.Chat.Core.Data.ActionPreviewStatus;
using SessionTier = Biatec.Chat.Core.Data.SessionTier;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Auth;
using Biatec.Chat.Api.Auth.Elevation;
using Biatec.Chat.Api.Chat.Confirm;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Consent;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Vault;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Consent;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using AlgoEncoder = Algorand.Utils.Encoder;
using ConsentRequestStatus = Biatec.Chat.Core.Data.ConsentStatus;

namespace Biatec.Chat.Tests.Consent;

/// <summary>
/// The consent orchestrator (T-049) against an in-process mock of Biatec OIDC: the wallet sign endpoint answers the
/// step-up challenge until it is shown the elevated token, and the CIBA token endpoint speaks the wire format of the
/// consent protocol. Needs the docker-compose postgres. No network is touched.
/// </summary>
public sealed class ConsentOrchestratorTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Network = "algorand-testnet";
    private const string Issuer = "https://oidc.example.test";
    private const string Resource = "https://mcp.example.test";
    private const string AuthReqId = "ciba_8257c10231dd9b46e2f877484cf0b2a3";
    private const string ConsentToken = "consent_TOKEN_abc";
    private const string Receipt = "{\"version\":1,\"alg\":\"ES256\",\"signature\":\"AAAA\",\"signed\":{\"decision\":\"approve\",\"binding_code\":\"483920\"},\"device\":{\"kid\":\"dev-1\"}}";

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

    /// <summary>Mock Biatec OIDC, wallet side: asks for consent until it is shown the elevated token.</summary>
    private sealed class MockWallet(Account account) : IWalletSigningClient
    {
        public readonly List<(string Bearer, WalletSignRequest Request)> Calls = [];
        public bool ThrowOnConsentToken;
        public Task<WalletSignOutcome> SignAsync(string bearer, WalletSignRequest request, CancellationToken ct = default)
        {
            Calls.Add((bearer, request));
            if (bearer == ConsentToken && ThrowOnConsentToken) throw new InvalidOperationException("boom");
            if (bearer != ConsentToken) return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.StepUp(AuthReqId, "483920", 300, 2));
            var signed = request.Transactions.Select(t => Convert.ToBase64String(AlgoEncoder.EncodeToMsgPackOrdered(Transaction.FromBase64String(t).Sign(account)))).ToList();
            return Task.FromResult<WalletSignOutcome>(new WalletSignOutcome.Signed(signed, "1200.00"));
        }
    }

    /// <summary>Mock Biatec OIDC, token side: POST /token with the CIBA grant, GET /consent/settings.</summary>
    private sealed class MockConsentServer : HttpMessageHandler
    {
        public string State = "pending"; // pending | approved | denied | code_failed | expired | slow_down
        public bool SettingsFail;
        public readonly List<Dictionary<string, string>> TokenRequests = [];
        public bool Redeemed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            Assert.Equal(Issuer, url.GetLeftPart(UriPartial.Authority));
            if (url.AbsolutePath == "/consent/settings")
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                return SettingsFail ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(200, "{\"threshold_eur\":\"250.00\",\"device_enrolled\":true,\"device_active\":true,\"client\":{\"always_ask\":false,\"paused_until\":null,\"cooling_off_until\":null,\"first_use\":false}}");
            }
            Assert.Equal("/token", url.AbsolutePath);
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            var f = form.AllKeys.ToDictionary(k => k!, k => form[k]!);
            TokenRequests.Add(f);
            if (f["client_id"] != "biatec-chat" || f["client_secret"] != "test-client-secret") return Json(401, "{\"error\":\"invalid_client\"}");
            if (f["grant_type"] != "urn:openid:params:grant-type:ciba" || f["auth_req_id"] != AuthReqId || f["resource"] != Resource) return Json(400, "{\"error\":\"invalid_grant\"}");
            if (Redeemed) return Json(400, "{\"error\":\"invalid_grant\"}");
            switch (State)
            {
                case "pending": return Json(400, "{\"error\":\"authorization_pending\"}");
                case "slow_down": return Json(400, "{\"error\":\"slow_down\"}");
                case "denied": return Json(400, "{\"error\":\"access_denied\"}");
                case "code_failed": return Json(400, "{\"error\":\"access_denied\",\"reason\":\"code_failed\"}");
                case "expired": return Json(400, "{\"error\":\"expired_token\"}");
                default:
                    Redeemed = true;
                    return Json(200, "{\"access_token\":\"" + ConsentToken + "\",\"token_type\":\"Bearer\",\"expires_in\":300,\"scope\":\"openid sign\",\"consent_receipt\":" + Receipt + "}");
            }
        }

        private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeMetadata : IOidcMetadataProvider
    {
        public Task<OidcMetadata> GetAsync(CancellationToken ct) =>
            Task.FromResult(new OidcMetadata(Issuer, new Uri(Issuer + "/authorize"), new Uri(Issuer + "/token"), new Uri(Issuer + "/jwks")));
        public Task<IReadOnlyList<Microsoft.IdentityModel.Tokens.SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeSecrets : ISecretResolver
    {
        public string Resolve(string secretRef) => "test-client-secret";
    }

    private sealed class FakeMcp : IBiatecMcpClient
    {
        public readonly List<string> Submits = [];
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default)
        {
            Submits.Add(signedTransactionBase64);
            return Task.FromResult(new McpToolResult(false, "", null));
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
        public Task<TxConfirmation> WaitAsync(string network, string txId, ulong lastValid, CancellationToken ct = default) =>
            Task.FromResult(new TxConfirmation(TxConfirmationState.Confirmed, 4242));
    }

    private sealed class FakeEvidence : IEvidenceLog
    {
        public readonly List<(string Kind, JsonObject Payload)> Entries = [];
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, string? anchorRef = null, CancellationToken ct = default)
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
    private readonly MemoryVault _vault = new();
    private readonly FakeMcp _mcp = new();
    private readonly FakeEvidence _evidence = new();
    private readonly MockConsentServer _server = new();
    private MockWallet _wallet = null!;
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
        _wallet = new MockWallet(_user);
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
            AccessToken = "ACCESS-TOKEN-SECRET", AccessExpiresAt = _clock.Now.AddMinutes(10), RefreshToken = "REFRESH-TOKEN-SECRET",
            Scopes = ["openid", "sign"], AbsoluteExpiresAt = _session.AbsoluteExpiresAt,
        };
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private static PaymentTransaction Pay(Address sender, ulong amount = 5_000_000) => new()
    {
        Sender = sender,
        Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
        Amount = amount, Fee = 1000, FirstValid = 710399, LastValid = 711399,
        GenesisId = "testnet-v1.0",
        GenesisHash = new Digest(Convert.FromBase64String("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")),
        Note = Encoding.UTF8.GetBytes("t"),
    };

    private async Task<(GroupedTransactions Group, Guid PreviewId)> BuildPreviewAsync()
    {
        var group = AlgorandGroup.Ensure([Pay(_user.Address)]);
        await using var db = NewContext();
        var snap = await new ActionPreviewService(db, _clock).CreateAsync(new NewPreviewSpec(_session.ConversationId, "wallet.transfer", "{\"intent\":\"send\"}",
        [
            new NewStepSpec(ActionStepKind.Sign, group.GroupHash, _clock.Now.AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(group.ToBase64()),
                SenderAddress: _user.Address.EncodeAsString(), Network: Network),
        ]));
        return (group, snap.Preview.Id);
    }

    private ConfirmService NewConfirm(ChatDbContext db) =>
        new(db, new ActionPreviewService(db, _clock), new ScopeGuard(_vault, _clock), _vault, _wallet, _mcp, new FakeConfirmer(), _evidence, _clock,
            NullLogger<ConfirmService>.Instance);

    private ConsentOrchestrator NewOrchestrator(ChatDbContext db) =>
        new(db, NewConfirm(db), new CibaTokenClient(new HttpClient(_server)), new FakeMetadata(), new FakeSecrets(),
            Options.Create(new OidcOptions { Issuer = Issuer, McpResource = Resource }), _evidence, _clock, NullLogger<ConsentOrchestrator>.Instance);

    private async Task<ConfirmResult> ConfirmAsync(Guid previewId, string groupHash)
    {
        await using var db = NewContext();
        return await NewConfirm(db).ConfirmAsync(_tenant, _session, previewId, groupHash, Biatec.Chat.Api.Chat.Confirm.ConfirmService.BackendOrigin(_tenant), CancellationToken.None);
    }

    private async Task<ConsentAdvance> AdvanceAsync(Guid previewId)
    {
        await using var db = NewContext();
        return await NewOrchestrator(db).AdvanceAsync(_tenant, _session, previewId, CancellationToken.None);
    }

    private async Task<(ActionPreview Preview, ActionStep Step, ConsentRequest Consent)> LoadAsync(Guid previewId)
    {
        await using var db = NewContext();
        return (await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == previewId), await db.ActionSteps.AsNoTracking().SingleAsync(s => s.PreviewId == previewId),
            await db.ConsentRequests.AsNoTracking().SingleAsync());
    }

    [Fact]
    public async Task Poll_interval_is_kept_and_slow_down_adds_five_seconds_up_to_the_cap()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        Assert.Equal(2, (await LoadAsync(b.PreviewId)).Item3.PollIntervalSeconds); // what the step-up returned

        var pending = await AdvanceAsync(b.PreviewId);
        Assert.Equal(TimeSpan.FromSeconds(2), pending.PollAfter);

        _server.State = "slow_down";
        var slower = await AdvanceAsync(b.PreviewId);
        Assert.Equal(ConsentState.Pending, slower.Status.State);
        Assert.Equal(TimeSpan.FromSeconds(7), slower.PollAfter);
        Assert.Equal(7, (await LoadAsync(b.PreviewId)).Item3.PollIntervalSeconds); // kept: a new stream starts slow too

        // Back to pending: the raised interval stays; and slow_down never goes past the cap.
        _server.State = "pending";
        Assert.Equal(TimeSpan.FromSeconds(7), (await AdvanceAsync(b.PreviewId)).PollAfter);
        _server.State = "slow_down";
        for (var i = 0; i < 20; i++) await AdvanceAsync(b.PreviewId);
        Assert.Equal(ConsentOrchestrator.MaxPollIntervalSeconds, (await LoadAsync(b.PreviewId)).Item3.PollIntervalSeconds);
    }

    [Fact]
    public async Task EndToEnd_Challenge_Approve_Signed()
    {
        var b = await BuildPreviewAsync();
        var confirmed = await ConfirmAsync(b.PreviewId, b.Group.GroupHash);

        // The challenge: the card shows the binding code and the consent window, and the first call sent the maxConsentSeconds bound.
        Assert.Equal(ConfirmOutcome.ConsentPending, confirmed.Outcome);
        Assert.Equal("483920", confirmed.Consent!.BindingCode);
        Assert.InRange(_wallet.Calls[0].Request.MaxConsentSeconds!.Value, 1, 1500);
        Assert.Equal("ACCESS-TOKEN-SECRET", _wallet.Calls[0].Bearer);

        // The phone has not answered yet.
        var waiting = await AdvanceAsync(b.PreviewId);
        Assert.Equal(ConsentState.Pending, waiting.Status.State);
        Assert.Equal("483920", waiting.Status.BindingCode);
        Assert.Null(waiting.Result);
        Assert.Empty(_mcp.Submits);

        // slow_down keeps waiting too.
        _server.State = "slow_down";
        Assert.Equal(ConsentState.Pending, (await AdvanceAsync(b.PreviewId)).Status.State);

        // Approve on the phone: the next poll signs with the elevated token and submits the same previewed group.
        _server.State = "approved";
        var done = await AdvanceAsync(b.PreviewId);
        Assert.Equal(ConsentState.Approved, done.Status.State);
        Assert.Equal(ConfirmOutcome.Landed, done.Result!.Outcome);
        Assert.Equal(b.Group.TxIds, done.Result.TxIds);

        Assert.Equal(2, _wallet.Calls.Count);
        Assert.Equal(ConsentToken, _wallet.Calls[1].Bearer);
        Assert.Equal(b.Group.ToBase64(), _wallet.Calls[1].Request.Transactions);
        Assert.Single(_mcp.Submits);
        Assert.All(_server.TokenRequests, r => Assert.Equal(Resource, r["resource"]));

        var (preview, step, consent) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Completed, preview.Status);
        Assert.Equal(StepStatus.Confirmed, step.Status);
        Assert.Equal(ConsentRequestStatus.Approved, consent.Status);

        // Polling again after the end changes nothing and signs nothing twice.
        var again = await AdvanceAsync(b.PreviewId);
        Assert.Equal(ConsentState.Approved, again.Status.State);
        Assert.Equal(2, _wallet.Calls.Count);
        Assert.Single(_mcp.Submits);
    }

    [Fact]
    public async Task Receipt_IsStoredVerbatim_AndRecordedInEvidence_AndNoTokenLeaks()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _server.State = "approved";
        await AdvanceAsync(b.PreviewId);

        var (_, step, consent) = await LoadAsync(b.PreviewId);
        using (var stored = JsonDocument.Parse(consent.Receipt!))
            Assert.Equal("dev-1", stored.RootElement.GetProperty("device").GetProperty("kid").GetString());
        // jsonb keeps the JSON value, not the byte layout; the exact text is in the evidence entry below.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Receipt), JsonNode.Parse(consent.Receipt!)));

        var recorded = Assert.Single(_evidence.Entries, e => e.Kind == EvidenceKinds.ConsentReceipt).Payload;
        Assert.Equal(Receipt, (string?)recorded["receipt"]);
        Assert.Equal(AuthReqId, (string?)recorded["authReqId"]);
        Assert.Equal(step.Id.ToString(), (string?)recorded["stepId"]);
        Assert.Equal(b.Group.GroupHash, (string?)recorded["groupHash"]);
        Assert.DoesNotContain(ConsentToken, string.Concat(_evidence.Entries.Select(e => e.Payload.ToJsonString())));
        // The consent_receipt entry comes before the signing outcome of the retry.
        Assert.Contains("action_submitted", _evidence.Entries.Select(e => e.Kind));
    }

    [Theory]
    [InlineData("denied", "deny")]
    [InlineData("code_failed", "code_failed")]
    public async Task Deny_CancelsTheAction_AndNothingIsSigned(string serverState, string reason)
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _server.State = serverState;
        var r = await AdvanceAsync(b.PreviewId);

        Assert.Equal(ConsentState.Denied, r.Status.State);
        Assert.Equal(ConfirmOutcome.Failed, r.Result!.Outcome);
        Assert.Equal("consent_denied", r.Result.ErrorCode);
        Assert.Single(_wallet.Calls);
        Assert.Empty(_mcp.Submits);

        var (preview, step, consent) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Cancelled, preview.Status);
        Assert.Equal(StepStatus.Cancelled, step.Status);
        Assert.Equal(ConsentRequestStatus.Denied, consent.Status);
        Assert.Null(consent.Receipt);
        var denied = Assert.Single(_evidence.Entries, e => e.Kind == "consent_denied").Payload;
        Assert.Equal(reason, (string?)denied["reason"]);

        // Stays denied on later polls; the token endpoint is not asked again.
        var polls = _server.TokenRequests.Count;
        Assert.Equal(ConsentState.Denied, (await AdvanceAsync(b.PreviewId)).Status.State);
        Assert.Equal(polls, _server.TokenRequests.Count);
    }

    [Fact]
    public async Task ExpiredAtOidc_ExpiresThePreview_SoItIsRebuilt()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _server.State = "expired";
        var r = await AdvanceAsync(b.PreviewId);

        Assert.Equal(ConsentState.TimedOut, r.Status.State);
        Assert.Equal("consent_expired", r.Result!.ErrorCode);
        var (preview, step, consent) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Expired, preview.Status);
        Assert.Equal(StepStatus.Expired, step.Status);
        Assert.Equal(ConsentRequestStatus.Expired, consent.Status);

        // The old card can no longer be confirmed: it has to be rebuilt, and a rebuilt preview with a new hash is accepted.
        await Assert.ThrowsAsync<PreviewException>(() => ConfirmAsync(b.PreviewId, b.Group.GroupHash));
        Assert.Empty(_mcp.Submits);
        var fresh = AlgorandGroup.Ensure([Pay(_user.Address, 6_000_000)]);
        await using var db = NewContext();
        var rebuilt = await new ActionPreviewService(db, _clock).RebuildAsync(_session.ConversationId, b.PreviewId, "{\"intent\":\"send\"}",
            [new NewStepSpec(ActionStepKind.Sign, fresh.GroupHash, _clock.Now.AddSeconds(1500), UnsignedGroup: JsonSerializer.Serialize(fresh.ToBase64()),
                SenderAddress: _user.Address.EncodeAsString(), Network: Network)]);
        Assert.Equal(ActionPreviewStatus.Proposed, rebuilt.Preview.Status);
    }

    private async Task<DateTimeOffset> SessionActivityAsync()
    {
        await using var db = NewContext();
        return (await db.ChatSessions.AsNoTracking().SingleAsync(s => s.Id == _session.Id)).LastActivityAt;
    }

    [Fact]
    public async Task Approval_RestartsTheSessionIdleWindow()
    {
        // T-080: the open-consent exemption ends at approval; signing must not run on a session that is already due.
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _clock.Now = _clock.Now.AddSeconds(240);
        _server.State = "approved";
        Assert.Equal(ConsentState.Approved, (await AdvanceAsync(b.PreviewId)).Status.State);
        Assert.Equal(_clock.Now, await SessionActivityAsync());
    }

    [Fact]
    public async Task TimeOut_RestartsTheSessionIdleWindow()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _clock.Now = _clock.Now.AddSeconds(301);
        Assert.Equal(ConsentState.TimedOut, (await AdvanceAsync(b.PreviewId)).Status.State);
        Assert.Equal(_clock.Now, await SessionActivityAsync());
    }

    [Fact]
    public async Task ConsentWindowElapsed_ExpiresWithoutAskingOidc()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _clock.Now = _clock.Now.AddSeconds(301);
        var r = await AdvanceAsync(b.PreviewId);

        Assert.Equal(ConsentState.TimedOut, r.Status.State);
        Assert.Empty(_server.TokenRequests);
        Assert.Equal(ActionPreviewStatus.Expired, (await LoadAsync(b.PreviewId)).Preview.Status);
    }

    [Fact]
    public async Task ApprovalAfterPreviewExpiry_SignsNothing_AndExpiresThePreview()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        // A consent window that outlives the step (for example a longer window granted by Biatec OIDC).
        await using (var db = NewContext())
        {
            foreach (var c in await db.ConsentRequests.ToListAsync()) c.ExpiresAt = _clock.Now.AddSeconds(3000);
            await db.SaveChangesAsync();
        }
        _clock.Now = _clock.Now.AddSeconds(1501);
        _server.State = "approved";
        var r = await AdvanceAsync(b.PreviewId);

        Assert.Equal("preview_expired", r.Result!.ErrorCode);
        Assert.Single(_wallet.Calls);
        Assert.Empty(_mcp.Submits);
        var (preview, step, _) = await LoadAsync(b.PreviewId);
        Assert.Equal(ActionPreviewStatus.Expired, preview.Status);
        Assert.Equal(StepStatus.Expired, step.Status);
        Assert.Contains(_evidence.Entries, e => e.Kind == "consent_expired" && (string?)e.Payload["reason"] == "preview_expired");
    }

    [Fact]
    public async Task ResumeThrowing_DoesNotLeaveTheStepAwaitingConsent()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        _wallet.ThrowOnConsentToken = true;
        _server.State = "approved";
        var r = await AdvanceAsync(b.PreviewId);

        Assert.Equal(ConfirmOutcome.Failed, r.Result!.Outcome);
        Assert.Equal("resume_failed", r.Result.ErrorCode);
        Assert.Empty(_mcp.Submits);
        Assert.Equal(StepStatus.Expired, (await LoadAsync(b.PreviewId)).Step.Status);
    }

    [Fact]
    public async Task ApprovalWithoutAReceipt_IsNotAccepted()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        var handler = new MockNoReceipt();
        await using var db = NewContext();
        var orchestrator = new ConsentOrchestrator(db, NewConfirm(db), new CibaTokenClient(new HttpClient(handler)), new FakeMetadata(), new FakeSecrets(),
            Options.Create(new OidcOptions { Issuer = Issuer, McpResource = Resource }), _evidence, _clock, NullLogger<ConsentOrchestrator>.Instance);
        var r = await orchestrator.AdvanceAsync(_tenant, _session, b.PreviewId, CancellationToken.None);

        Assert.NotEqual(ConsentState.Approved, r.Status.State);
        Assert.Equal("receipt_missing", r.Result!.ErrorCode);
        Assert.Single(_wallet.Calls);
        Assert.Empty(_mcp.Submits);
    }

    private sealed class MockNoReceipt : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"consent_x\",\"token_type\":\"Bearer\",\"expires_in\":300}", Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task UnknownPreview_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<PreviewException>(() => AdvanceAsync(Guid.NewGuid()));
        Assert.Equal(PreviewError.NotFound, ex.Error);
    }

    [Fact]
    public async Task OidcOutageWhilePolling_KeepsWaiting()
    {
        var b = await BuildPreviewAsync();
        await ConfirmAsync(b.PreviewId, b.Group.GroupHash);
        await using var db = NewContext();
        var down = new ConsentOrchestrator(db, NewConfirm(db), new CibaTokenClient(new HttpClient(new Down())), new FakeMetadata(), new FakeSecrets(),
            Options.Create(new OidcOptions { Issuer = Issuer, McpResource = Resource }), _evidence, _clock, NullLogger<ConsentOrchestrator>.Instance);
        var r = await down.AdvanceAsync(_tenant, _session, b.PreviewId, CancellationToken.None);
        Assert.Equal(ConsentState.Pending, r.Status.State);
        Assert.Equal(StepStatus.AwaitingConsent, (await LoadAsync(b.PreviewId)).Step.Status);
    }

    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("down");
    }

    [Fact]
    public async Task Prediction_UsesConsentSettings_AndFallsBackWhenTheyFail()
    {
        var predictor = new ConsentPredictor(_vault, new ConsentSettingsClient(new HttpClient(_server)),
            Options.Create(new OidcOptions { Issuer = Issuer, McpResource = Resource }), NullLogger<ConsentPredictor>.Instance);

        Assert.Equal(ConsentPrediction.AskedText, await predictor.PredictAsync(_session, 1200m, CancellationToken.None));
        Assert.Equal(ConsentPrediction.NotExpectedText, await predictor.PredictAsync(_session, 20m, CancellationToken.None));
        Assert.Equal(ConsentPrediction.FallbackText, await predictor.PredictAsync(_session, null, CancellationToken.None));

        _server.SettingsFail = true;
        Assert.Equal("This may need approval on your phone.", await predictor.PredictAsync(_session, 1200m, CancellationToken.None));
    }

    [Fact]
    public void Prediction_WithoutADevice_TellsTheUserToEnroll()
    {
        var s = new ConsentSettings(250m, DeviceEnrolled: false, DeviceActive: false, AlwaysAsk: false, CoolingOff: false, FirstUse: false);
        Assert.Equal(ConsentPrediction.NoDeviceText, ConsentPrediction.Describe(s, 1000m));
        Assert.Equal(ConsentPrediction.FallbackText, ConsentPrediction.Describe(null, 1000m));
    }

    [Fact]
    public async Task CibaClient_MapsTheProtocolErrors()
    {
        var client = new CibaTokenClient(new HttpClient(_server));
        var ep = new Uri(Issuer + "/token");
        Task<CibaPollOutcome> Poll(string id = AuthReqId) => client.PollAsync(ep, "biatec-chat", "test-client-secret", Resource, id);

        Assert.IsType<CibaPollOutcome.Pending>(await Poll());
        Assert.IsType<CibaPollOutcome.Gone>(await Poll("unknown"));
        _server.State = "slow_down";
        Assert.True(Assert.IsType<CibaPollOutcome.Pending>(await Poll()).SlowDown);
        _server.State = "code_failed";
        Assert.Equal("code_failed", Assert.IsType<CibaPollOutcome.Denied>(await Poll()).Reason);
        _server.State = "expired";
        Assert.IsType<CibaPollOutcome.Expired>(await Poll());
        _server.State = "approved";
        var ok = Assert.IsType<CibaPollOutcome.Approved>(await Poll());
        Assert.Equal(ConsentToken, ok.AccessToken);
        Assert.Equal(Receipt, ok.Receipt);
        Assert.DoesNotContain(ConsentToken, ok.ToString());
        Assert.IsType<CibaPollOutcome.Unavailable>(await new CibaTokenClient(new HttpClient(new Down())).PollAsync(ep, "c", "s", Resource, AuthReqId));
    }
}
