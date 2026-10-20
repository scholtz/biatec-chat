using Biatec.Chat.Api.Data;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Tests.Data;

/// <summary>
/// Needs the docker-compose postgres (or Postgres__AdminConnectionString). Each run migrates a fresh, empty,
/// uniquely named database, so it also proves the migrations apply to an empty server.
/// </summary>
public sealed class DataModelTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");

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
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    [Fact]
    public async Task Model_HasNoPendingChanges_AgainstTheMigration()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task EveryEntity_RoundTripsOneRow()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var conv = Guid.NewGuid();
        var preview = Guid.NewGuid();
        var step = Guid.NewGuid();
        var anchor = Guid.NewGuid();

        await using (var db = NewContext())
        {
            db.AddRange(
                new Conversation { Id = conv, TenantId = "t1", OidcSub = "sub", CreatedAt = now, LastMessageAt = now, EndedAt = now },
                new ChatSession { Id = Guid.NewGuid(), ConversationId = conv, Tier = SessionTier.Authenticated, TenantId = "t1", OidcSub = "sub", CreatedAt = now, LastActivityAt = now, AbsoluteExpiresAt = now.AddHours(8) },
                new Message { ConversationId = conv, Seq = 1, Role = MessageRole.Assistant, Content = "hi", ToolCalls = "[{\"n\":1}]", ToolResults = "[]", ModelId = "eu.anthropic.claude-haiku-4-5-20251001-v1:0", Usage = "{\"in\":1}" },
                new ActionPreview { Id = preview, ConversationId = conv, Capability = "dex.swap", CanonicalJson = "{\"a\":1}", PredictedValueEur = 12.34m, PredictedNeedsConsent = true, ExpiresAt = now.AddMinutes(5), Status = ActionPreviewStatus.InProgress, RebuiltAsPreviewId = Guid.NewGuid() },
                new ActionStep { Id = step, PreviewId = preview, Seq = 0, Kind = ActionStepKind.Sign, SubjectHash = "abc", UnsignedGroup = "[\"AAA\"]", SenderAddress = "ADDR", Network = "testnet", FirstValid = 1, LastValid = 1001, ExpiresAt = now.AddMinutes(5), Status = StepStatus.AwaitingConsent, TxIds = ["TX1", "TX2"] },
                new ConsentRequest { Id = Guid.NewGuid(), StepId = step, SubjectHash = "abc", AuthReqId = "auth-req-1", BindingCode = "1234", ExpiresAt = now.AddMinutes(2), Status = ConsentStatus.Pending, Receipt = "{\"r\":true}" },
                new EvidenceEntry { ConversationId = conv, Seq = 1, Kind = "message", AnchorRef = "TXA", Payload = "{\"p\":1}", Salt = "salt", PayloadHash = "ph", PrevHash = "prev", EntryHash = "eh", Kid = "k1", SignedAt = now, Signature = "sig" },
                new EvidenceAnchor { Id = anchor, SequenceNumber = 7, PreviousAnchorTxId = "PREV", AnchorTxId = "TXA", MerkleRoot = "root", AnchoredAt = now, GapFrom = "3" },
                new EvidenceAnchorLeaf { AnchorId = anchor, ConversationId = conv, AnchoredSeq = 1, HeadHash = "eh", LeafIndex = 0, SiblingHashes = "[{\"hash\":\"h\",\"side\":\"left\"}]" },
                new PublicConversationState { ConversationId = conv, CapabilityHash = "cap", ContinuationTokenHash = "tok", PreviousTokenHash = "old", LastReplyRef = "reply", AdoptedAt = now },
                new PopupAuthRequest { Id = Guid.NewGuid(), ConversationId = conv, Purpose = PopupAuthPurpose.Elevate, NonceHash = "n", Status = PopupAuthStatus.Pending, ExpiresAt = now.AddMinutes(10) },
                new PendingRevocation { Id = Guid.NewGuid(), TenantId = "t1", EncryptedRefreshToken = [1, 2, 3], ExpiresAt = now.AddDays(30), Attempts = 2 },
                new Tenant { Id = "t1", Host = "chat.example.test", SignInPageUrl = "https://example.test/signin", BedrockBudgets = "{\"daily\":10}", SafeApprovalCaps = "{\"perHour\":5}", ConsentThresholdCeilingEur = 100m, LimitsUpdateEnabled = true, X402Enabled = false, OidcClientId = "cid", ClientSecretRef = "secret/ref", RedirectUri = "https://chat.example.test/cb", AllowedOrigins = ["https://a.test", "https://b.test"], ClientKeys = "[{\"kid\":\"k\"}]", SoftCapEurPerHour = 50m, TrustAnchorVerified = true, DefaultModel = "m", Suggestions = "[\"What is Biatec?\"]" },
                new SafeApprovalCount { Id = Guid.NewGuid(), OidcSub = "sub", TenantId = "t1", Kind = SafeApprovalKind.Approve, ValueEur = 99.5m, ApprovedAt = now },
                new SoftCapSpend { Id = Guid.NewGuid(), OidcSub = "sub", TenantId = "t1", Status = SoftCapStatus.Final, ReservedAt = now, SpentAt = now, AmountEur = 3.21m, ValueSource = "oidc" });
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var c = await read.Conversations.SingleAsync();
        Assert.Equal(("t1", "sub", now), (c.TenantId, c.OidcSub, c.EndedAt));
        Assert.Equal(SessionTier.Authenticated, (await read.ChatSessions.SingleAsync()).Tier);
        var m = await read.Messages.SingleAsync();
        Assert.Equal(("hi", 1L), (m.Content, m.Seq));
        Assert.Contains("\"n\"", m.ToolCalls);
        var p = await read.ActionPreviews.SingleAsync();
        Assert.Equal((12.34m, ActionPreviewStatus.InProgress), (p.PredictedValueEur, p.Status));
        Assert.Equal(["TX1", "TX2"], (await read.ActionSteps.SingleAsync()).TxIds);
        Assert.Equal("auth-req-1", (await read.ConsentRequests.SingleAsync()).AuthReqId);
        var e = await read.EvidenceEntries.SingleAsync();
        Assert.Equal(("eh", "TXA"), (e.EntryHash, e.AnchorRef));
        Assert.Equal(7, (await read.EvidenceAnchors.SingleAsync()).SequenceNumber);
        Assert.Equal(0, (await read.EvidenceAnchorLeaves.SingleAsync()).LeafIndex);
        Assert.Equal("tok", (await read.PublicConversationStates.SingleAsync()).ContinuationTokenHash);
        Assert.Equal(PopupAuthPurpose.Elevate, (await read.PopupAuthRequests.SingleAsync()).Purpose);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await read.PendingRevocations.SingleAsync()).EncryptedRefreshToken);
        var t = await read.Tenants.SingleAsync();
        Assert.Equal(["https://a.test", "https://b.test"], t.AllowedOrigins);
        Assert.Equal(50m, t.SoftCapEurPerHour);
        Assert.Equal(99.5m, (await read.SafeApprovalCounts.SingleAsync()).ValueEur);
        Assert.Equal("oidc", (await read.SoftCapSpends.SingleAsync()).ValueSource);
    }

    [Fact]
    public async Task EvidenceEntry_RejectsDuplicateConversationAndSeq()
    {
        var conv = Guid.NewGuid();
        EvidenceEntry Entry() => new()
        {
            ConversationId = conv, Seq = 1, Kind = "message", PayloadHash = "p", PrevHash = "0", EntryHash = "e",
            Kid = "k", SignedAt = DateTimeOffset.UtcNow, Signature = "s",
        };

        await using (var db = NewContext())
        {
            db.EvidenceEntries.Add(Entry());
            await db.SaveChangesAsync();
        }

        await using var dup = NewContext();
        dup.EvidenceEntries.Add(Entry());
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
    }
}
