using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Evidence.Anchoring;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Biatec.Chat.Tests.Evidence.Anchoring;

/// <summary>Merkle vectors are written out independently of MerkleTree, straight from the spec.</summary>
public class AnchoringMerkleTests
{
    private static byte[] Sha(byte prefix, params byte[][] parts)
    {
        var all = new List<byte> { prefix };
        foreach (var p in parts) all.AddRange(p);
        return SHA256.HashData(all.ToArray());
    }

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static string H(char c) => new(c, 64);

    // JCS of {conversationId, anchoredSeq, headHash}: keys sorted, so anchoredSeq, conversationId, headHash.
    private static byte[] Leaf(Guid id, long seq, string head) =>
        Sha(0x00, Encoding.UTF8.GetBytes($"{{\"anchoredSeq\":{seq},\"conversationId\":\"{id:D}\",\"headHash\":\"{head}\"}}"));

    [Fact]
    public void SingleLeaf_RootIsTheLeafHash()
    {
        var r = MerkleTree.Build([new AnchorLeafInput(A, 3, H('1'))]);
        Assert.Equal(Hex(Leaf(A, 3, H('1'))), r.Root);
        Assert.Empty(r.Proofs[0].Siblings);
    }

    [Fact]
    public void TwoLeaves_NodeIsSha256_0x01_Left_Right_AndLeavesAreSortedByConversationId()
    {
        // Passed in reverse order on purpose.
        var r = MerkleTree.Build([new AnchorLeafInput(B, 2, H('2')), new AnchorLeafInput(A, 1, H('1'))]);
        var expected = Sha(0x01, Leaf(A, 1, H('1')), Leaf(B, 2, H('2')));
        Assert.Equal(Hex(expected), r.Root);
        Assert.Equal(A, r.Proofs[0].Leaf.ConversationId);
        Assert.Equal(0, r.Proofs[0].LeafIndex);
        Assert.Equal("right", r.Proofs[0].Siblings[0].Side);
        Assert.Equal(Hex(Leaf(B, 2, H('2'))), r.Proofs[0].Siblings[0].Hash);
        Assert.Equal("left", r.Proofs[1].Siblings[0].Side);
    }

    [Fact]
    public void ThreeLeaves_TheUnpairedNodeIsPromotedNotDuplicated()
    {
        var r = MerkleTree.Build([new AnchorLeafInput(C, 3, H('3')), new AnchorLeafInput(A, 1, H('1')), new AnchorLeafInput(B, 2, H('2'))]);
        var ab = Sha(0x01, Leaf(A, 1, H('1')), Leaf(B, 2, H('2')));
        var expected = Sha(0x01, ab, Leaf(C, 3, H('3'))); // C is promoted unchanged to level 1
        Assert.Equal(Hex(expected), r.Root);
        var duplicated = Sha(0x01, ab, Sha(0x01, Leaf(C, 3, H('3')), Leaf(C, 3, H('3'))));
        Assert.NotEqual(Hex(duplicated), r.Root);
        // C's path has exactly one sibling: the A|B node.
        Assert.Single(r.Proofs[2].Siblings);
        Assert.Equal(Hex(ab), r.Proofs[2].Siblings[0].Hash);
    }

    [Fact]
    public void FiveLeaves_MatchesAnIndependentFold()
    {
        var ids = Enumerable.Range(1, 5).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0])).ToList();
        var leaves = ids.Select((id, i) => new AnchorLeafInput(id, i + 1, new string((char)('a' + i), 64))).ToList();
        var level = leaves.OrderBy(l => l.ConversationId.ToString("D"), StringComparer.Ordinal)
            .Select(l => Leaf(l.ConversationId, l.AnchoredSeq, l.HeadHash)).ToList();
        while (level.Count > 1)
        {
            var next = new List<byte[]>();
            for (var i = 0; i < level.Count; i += 2) next.Add(i + 1 < level.Count ? Sha(0x01, level[i], level[i + 1]) : level[i]);
            level = next;
        }
        Assert.Equal(Hex(level[0]), MerkleTree.Build(leaves).Root);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(33)] [InlineData(100)]
    public void EveryProofRecomputesToTheRoot_AndTamperingBreaksIt(int n)
    {
        var leaves = Enumerable.Range(0, n).Select(i => new AnchorLeafInput(Guid.NewGuid(), i + 1, Hex(SHA256.HashData(BitConverter.GetBytes(i))))).ToList();
        var r = MerkleTree.Build(leaves);
        foreach (var p in r.Proofs)
        {
            Assert.Equal(r.Root, MerkleTree.ComputeRoot(p.Leaf.ConversationId, p.Leaf.AnchoredSeq, p.Leaf.HeadHash, p.Siblings));
            Assert.NotEqual(r.Root, MerkleTree.ComputeRoot(p.Leaf.ConversationId, p.Leaf.AnchoredSeq + 1, p.Leaf.HeadHash, p.Siblings));
            Assert.NotEqual(r.Root, MerkleTree.ComputeRoot(p.Leaf.ConversationId, p.Leaf.AnchoredSeq, H('f'), p.Siblings));
        }
        Assert.All(r.Proofs, p => Assert.True(p.Siblings.Count <= 20));
    }

    [Fact]
    public void DuplicateConversation_IsRejected() =>
        Assert.Throws<ArgumentException>(() => MerkleTree.Build([new AnchorLeafInput(A, 1, H('1')), new AnchorLeafInput(A, 2, H('2'))]));

    [Fact]
    public void Note_RoundTrips_CarriesRootSeqPrevAndGap_AndFitsTheLimit()
    {
        var note = new AnchorNote(H('a'), 7, "TXPREV", 5);
        var bytes = note.ToBytes();
        Assert.Equal("{\"gapFrom\":\"5\",\"prev\":\"TXPREV\",\"root\":\"" + H('a') + "\",\"seq\":7,\"v\":1}", Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.Length <= AnchorNote.MaxBytes);
        Assert.True(AnchorNote.TryParse(bytes, out var parsed));
        Assert.Equal(note, parsed);
        Assert.False(AnchorNote.TryParse("{\"v\":1,\"root\":\"zz\",\"seq\":1}"u8, out _));
        Assert.False(AnchorNote.TryParse("not json"u8, out _));
    }
}

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public void Advance(TimeSpan d) => _now += d;
    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class FakeBroadcaster : IAnchorBroadcaster
{
    public List<byte[]> Notes { get; } = [];
    public int FailNext { get; set; }
    public int Calls { get; private set; }
    public AnchorResolution? Resolution { get; set; }

    public Task<AnchorResolution> ResolveAsync(byte[] note, CancellationToken ct) =>
        Task.FromResult(Resolution ?? new AnchorResolution(AnchorResolutionKind.Expired));

    public Task<string> BroadcastAsync(byte[] note, CancellationToken ct)
    {
        Calls++;
        if (FailNext > 0) { FailNext--; throw new HttpRequestException("algod down"); }
        Notes.Add(note);
        return Task.FromResult("TX" + Notes.Count.ToString("D4") + new string('A', 8));
    }
}

/// <summary>Needs the docker-compose postgres, like the evidence log tests. No network, no real Algod.</summary>
public sealed class AnchoringServiceTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly DevEvidenceKeyStore _keys = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBroadcaster _chain = new();
    private readonly IOptions<AnchoringOptions> _opts = Options.Create(new AnchoringOptions { RetryDelay = TimeSpan.Zero, MaxBroadcastAttempts = 2 });

    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = true, MaxPoolSize = 40 }.ConnectionString;
    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);

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

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private AnchorService Service(ChatDbContext db) => new(db, _chain, _opts, NullLogger<AnchorService>.Instance, _time);
    private AnchoredEvidenceWriter Writer(ChatDbContext db) => new(new EvidenceLog(db, _keys, _time), db, _opts, _time);
    private static JsonObject P(string t) => new() { ["text"] = t };

    private async Task<(AnchorVerification V, IReadOnlyList<EvidenceEntry> Entries, AnchorExport Export)> VerifyAsync(Guid conv, Func<AnchorExport, AnchorExport>? tamper = null,
        Func<IReadOnlyList<AnchorRecord>, IReadOnlyList<AnchorRecord>>? chainTamper = null)
    {
        await using var db = NewContext();
        var entries = await new EvidenceLog(db, _keys, _time).GetChainAsync(conv);
        var export = await AnchorExporter.ExportAsync(db, conv);
        if (tamper is not null) export = tamper(export);
        var all = (await db.EvidenceAnchors.AsNoTracking().OrderBy(a => a.SequenceNumber).ToListAsync())
            .Select(a => new AnchorRecord(a.Id, a.SequenceNumber, a.AnchorTxId, a.MerkleRoot, a.PreviousAnchorTxId, a.GapFrom is null ? null : long.Parse(a.GapFrom))).ToList();
        IReadOnlyList<AnchorRecord> chain = all;
        if (chainTamper is not null) chain = chainTamper(chain);
        return (AnchorVerifier.Verify(entries, export.Proofs, export.Anchors, chain), entries, export);
    }

    [Fact]
    public async Task Tick_AnchorsOpenConversations_PersistsLeavesAndWritesTheNote()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await w.AppendFirstAsync(b, "message", P("b1"));

        var r = await Service(db).TickAsync();
        Assert.NotNull(r.Anchor);
        Assert.Equal(2, r.Leaves);
        Assert.Equal(1, r.Anchor!.SequenceNumber);
        Assert.Null(r.Anchor.PreviousAnchorTxId);

        Assert.True(AnchorNote.TryParse(_chain.Notes[0], out var note));
        Assert.Equal(r.Anchor.MerkleRoot, note!.Root);
        Assert.Equal(1, note.Seq);
        Assert.Null(note.Prev);

        var leaves = await db.EvidenceAnchorLeaves.AsNoTracking().ToListAsync();
        Assert.Equal(2, leaves.Count);
        foreach (var l in leaves)
            Assert.Equal(r.Anchor.MerkleRoot, MerkleTree.ComputeRoot(l.ConversationId, l.AnchoredSeq, l.HeadHash, AnchorService.ParseSiblings(l.SiblingHashes)));

        // The next tick covers both again (changed or not) and links to the previous transaction.
        _time.Advance(TimeSpan.FromMinutes(10));
        var r2 = await Service(db).TickAsync();
        Assert.Equal(2, r2.Anchor!.SequenceNumber);
        Assert.Equal(r.Anchor.AnchorTxId, r2.Anchor.PreviousAnchorTxId);
        Assert.True(AnchorNote.TryParse(_chain.Notes[1], out var note2));
        Assert.Equal(r.Anchor.AnchorTxId, note2!.Prev);
        Assert.Equal(2, r2.Leaves);
    }


    private sealed class BlockingBroadcaster : IAnchorBroadcaster
    {
        public int Calls;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> BroadcastAsync(byte[] note, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task;
            return "TXBLOCK" + n.ToString("D4") + "AAAA";
        }
    }

    [Fact]
    public async Task TwoReplicasTickingConcurrently_BroadcastExactlyOneTransaction()
    {
        await using (var seed = NewContext())
            await Writer(seed).AppendFirstAsync(Guid.NewGuid(), "message", P("a1"));

        var chain = new BlockingBroadcaster();
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var a = new AnchorService(dbA, chain, _opts, NullLogger<AnchorService>.Instance, _time);
        var b = new AnchorService(dbB, chain, _opts, NullLogger<AnchorService>.Instance, _time);

        var tickA = a.TickAsync();
        await chain.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // A is inside the broadcast, holding the lock
        var rb = await b.TickAsync();
        Assert.True(rb.Skipped);
        Assert.Null(rb.Anchor);
        chain.Release.SetResult();
        var ra = await tickA;

        Assert.False(ra.Skipped);
        Assert.NotNull(ra.Anchor);
        Assert.Equal(1, chain.Calls);
        await using var check = NewContext();
        Assert.Equal(1, await check.EvidenceAnchors.CountAsync());

        // The lock is released afterwards: the next tick on the other replica runs.
        _time.Advance(TimeSpan.FromMinutes(10));
        var rb2 = await b.TickAsync();
        Assert.False(rb2.Skipped);
    }

    private sealed class CancellingBroadcaster : IAnchorBroadcaster
    {
        public Task<string> BroadcastAsync(byte[] note, CancellationToken ct) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task TheLockIsReleased_WhenTheTickThrows()
    {
        await using (var seed = NewContext())
            await Writer(seed).AppendFirstAsync(Guid.NewGuid(), "message", P("a1"));
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AnchorService(dbA, new CancellingBroadcaster(), _opts, NullLogger<AnchorService>.Instance, _time).TickAsync());
        var rb = await new AnchorService(dbB, _chain, _opts, NullLogger<AnchorService>.Instance, _time).TickAsync();
        Assert.False(rb.Skipped);
    }

    [Fact]
    public async Task HostedService_WithoutAMnemonic_DoesNotTick()
    {
        var scopes = new ThrowingScopeFactory();
        var host = new AnchorHostedService(scopes, Options.Create(new AnchoringOptions { Enabled = true }), NullLogger<AnchorHostedService>.Instance);
        await host.StartAsync(CancellationToken.None);
        await host.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(0, scopes.Calls);
    }

    private sealed class ThrowingScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public int Calls;
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() { Calls++; throw new InvalidOperationException("must not tick"); }
    }

    [Fact]
    public async Task AnchorId_IsDerivedFromTheTxId_AndRelabelledBundleAnchorsAreRejected()
    {
        Guid a = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        var t1 = await Service(db).TickAsync();
        Assert.Equal(AnchorIds.FromTxId(t1.Anchor!.AnchorTxId), t1.Anchor.Id);
        _time.Advance(TimeSpan.FromMinutes(10));
        await w.AppendFirstAsync(Guid.NewGuid(), "message", P("x"));
        await Service(db).TickAsync();

        Assert.True((await VerifyAsync(a)).V.Valid);
        // Operator swaps the tx behind an anchor record, or changes its root.
        var relabel = await VerifyAsync(a, e => e with { Anchors = e.Anchors.Select(x => x with { TxId = e.Anchors[^1].TxId }).ToList() });
        Assert.False(relabel.V.Valid);
        var badRoot = await VerifyAsync(a, e => e with { Anchors = e.Anchors.Select(x => x with { Root = new string('0', x.Root.Length) }).ToList() });
        Assert.False(badRoot.V.Valid);
        var badId = await VerifyAsync(a, e => e with { Anchors = e.Anchors.Select(x => x with { Id = Guid.NewGuid() }).ToList() });
        Assert.False(badId.V.Valid);
    }

    [Fact]
    public async Task Tick_IgnoresLongIdleConversationsAlreadyAnchored()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await Service(db).TickAsync();
        _time.Advance(TimeSpan.FromHours(5));
        await w.AppendFirstAsync(b, "message", P("b1"));
        var r = await Service(db).TickAsync();
        Assert.Equal(1, r.Leaves); // only b; a is idle and fully anchored
        _time.Advance(TimeSpan.FromHours(5));
        Assert.Null((await Service(db).TickAsync()).Anchor);
    }

    [Fact]
    public async Task Tick_WithNothingDue_DoesNotBroadcast()
    {
        await using var db = NewContext();
        var r = await Service(db).TickAsync();
        Assert.Null(r.Anchor);
        Assert.Equal(0, _chain.Calls);
    }

    [Fact]
    public async Task IdleConversation_IsAnchoredOneLastTime_ThenDropped_AndResumeRecordsTheLatestAnchor()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await w.AppendFirstAsync(b, "message", P("b1"));
        await Service(db).TickAsync();                       // anchor 1: a, b

        for (var i = 0; i < 2; i++)                           // +10, +20 min: still within 30 min of a's last entry
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(2, (await Service(db).TickAsync()).Leaves);
        }
        // b keeps talking, a is idle.
        _time.Advance(TimeSpan.FromMinutes(10));              // +30: a is dropped
        await Writer(db).AppendFirstAsync(b, "message", P("b2"));
        var dropped = await Service(db).TickAsync();
        Assert.Equal(1, dropped.Leaves);
        Assert.DoesNotContain(await db.EvidenceAnchorLeaves.Where(l => l.AnchorId == dropped.Anchor!.Id).ToListAsync(), l => l.ConversationId == a);

        // a comes back: a resume entry carries the latest confirmed anchor.
        var resume = await w.ResumeIfDroppedAsync(a);
        Assert.NotNull(resume);
        Assert.Equal(AnchoredEvidenceWriter.ResumeKind, resume!.Kind);
        Assert.Equal(dropped.Anchor!.AnchorTxId, resume.AnchorRef);
        Assert.Null(await w.ResumeIfDroppedAsync(a)); // no second resume while it is active again

        _time.Advance(TimeSpan.FromMinutes(10));
        var again = await Service(db).TickAsync();
        Assert.Contains(await db.EvidenceAnchorLeaves.Where(l => l.AnchorId == again.Anchor!.Id).ToListAsync(), l => l.ConversationId == a && l.AnchoredSeq == resume.Seq);
    }

    [Fact]
    public async Task EndMarker_IsAnchoredByTheNextTick_ThenTheConversationLeavesTheTicks()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await w.AppendFirstAsync(b, "message", P("b1"));
        await Service(db).TickAsync();
        var end = await w.EndAsync(a);
        Assert.Equal("end", end.Kind);
        Assert.Null(end.AnchorRef);

        _time.Advance(TimeSpan.FromMinutes(10));
        var t2 = await Service(db).TickAsync();
        Assert.Contains(await db.EvidenceAnchorLeaves.Where(l => l.AnchorId == t2.Anchor!.Id).ToListAsync(), l => l.ConversationId == a && l.AnchoredSeq == end.Seq);

        _time.Advance(TimeSpan.FromMinutes(10));
        var t3 = await Service(db).TickAsync();
        Assert.DoesNotContain(await db.EvidenceAnchorLeaves.Where(l => l.AnchorId == t3.Anchor!.Id).ToListAsync(), l => l.ConversationId == a);

        var (v, _, _) = await VerifyAsync(a);
        Assert.True(v.Valid, string.Join("; ", v.Errors));
        Assert.True(v.EndAnchored);
        Assert.True(v.Verified);
    }

    [Fact]
    public async Task FirstEntry_RecordsTheLatestConfirmedAnchor_NotALostOne()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        Assert.Null((await w.AppendFirstAsync(a, "message", P("a1"))).AnchorRef);
        var t1 = await Service(db).TickAsync();
        _chain.FailNext = 2; // the next tick is lost (2 attempts)
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await Service(db).TickAsync()).Lost);
        var first = await w.AppendFirstAsync(b, "message", P("b1"));
        Assert.Equal(t1.Anchor!.AnchorTxId, first.AnchorRef);
    }

    [Fact]
    public async Task LostTick_IsRecordedAsAGapByTheNextAnchor_AndTheVerifierAcceptsOnlyDeclaredGaps()
    {
        var a = Guid.NewGuid();
        await using var db = NewContext();
        await Writer(db).AppendFirstAsync(a, "message", P("a1"));
        await Service(db).TickAsync();                        // seq 1

        _chain.FailNext = 2;                                  // seq 2 is lost after the retry
        _time.Advance(TimeSpan.FromMinutes(10));
        var lost = await Service(db).TickAsync();
        Assert.True(lost.Lost);
        Assert.Null(lost.Anchor);
        Assert.Equal(3, _chain.Calls);                        // 1 good + 2 failed attempts (retried once)

        _time.Advance(TimeSpan.FromMinutes(10));
        var next = await Service(db).TickAsync();             // lands as seq 3 and names the gap
        Assert.Equal(3, next.Anchor!.SequenceNumber);
        Assert.Equal("2", next.Anchor.GapFrom);
        Assert.True(AnchorNote.TryParse(_chain.Notes[1], out var note));
        Assert.Equal(2, note!.GapFrom);
        Assert.Equal(3, note.Seq);

        var (v, _, _) = await VerifyAsync(a);
        Assert.True(v.Valid, string.Join("; ", v.Errors));

        // The same chain without the declared gap is rejected.
        var (bad, _, _) = await VerifyAsync(a, chainTamper: c => c.Select(x => x.Seq == 3 ? x with { GapFrom = null } : x).ToList());
        Assert.False(bad.Valid);
        Assert.Contains(bad.Errors, e => e.Contains("gap"));

        // The tracker is cleared once the gap is recorded.
        _time.Advance(TimeSpan.FromMinutes(10));
        await Writer(db).AppendFirstAsync(a, "message", P("a2"));
        Assert.Null((await Service(db).TickAsync()).Anchor!.GapFrom);
    }

    [Fact]
    public async Task ExportCarriesProofsForEveryAnchor_AndTheVerifierChecksThem()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await w.AppendFirstAsync(b, "message", P("b1"));
        for (var i = 0; i < 3; i++)
        {
            await Service(db).TickAsync();
            await w.AppendFirstAsync(a, "message", P("more " + i));
            _time.Advance(TimeSpan.FromMinutes(10));
        }
        await w.EndAsync(a);
        await Service(db).TickAsync();

        var (v, entries, export) = await VerifyAsync(a);
        Assert.Equal(4, export.Proofs.Count);
        Assert.True(v.Valid, string.Join("; ", v.Errors));
        Assert.True(v.Verified);
        Assert.True(v.EndAnchored);
        Assert.Equal(entries[^1].Seq, export.Proofs[^1].AnchoredSeq);

        // Entries written after the last anchor are reported as signed but not yet anchored.
        await Writer(db).AppendFirstAsync(b, "message", P("late"));
        var (vb, _, _) = await VerifyAsync(b);
        Assert.True(vb.Valid, string.Join("; ", vb.Errors));
        Assert.False(vb.Verified);
        Assert.NotNull(vb.UnanchoredFromSeq);
    }

    [Fact]
    public async Task Verifier_RejectsTamperedProofsHeadsRootsAndMissingAnchors()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(a, "message", P("a1"));
        await w.AppendFirstAsync(b, "message", P("b1"));
        for (var i = 0; i < 3; i++) { await Service(db).TickAsync(); _time.Advance(TimeSpan.FromMinutes(10)); }

        // Missing middle proof.
        var (missing, _, _) = await VerifyAsync(a, e => e with { Proofs = e.Proofs.Where((_, i) => i != 1).ToList() });
        Assert.False(missing.Valid);

        // A flipped sibling.
        var (sib, _, _) = await VerifyAsync(a, e => e with
        {
            Proofs = e.Proofs.Select((p, i) => i == 0 ? p with { SiblingHashes = p.SiblingHashes.Select(s => s with { Hash = new string('0', 64) }).ToList() } : p).ToList(),
        });
        Assert.False(sib.Valid);

        // A head that does not equal the bundle's entryHash (the operator rewrote history and re-anchored).
        var (head, _, _) = await VerifyAsync(a, e => e with { Proofs = e.Proofs.Select((p, i) => i == 0 ? p with { HeadHash = new string('9', 64) } : p).ToList() });
        Assert.False(head.Valid);

        // A root on chain that differs from the one the proofs lead to.
        var (root, _, _) = await VerifyAsync(a, chainTamper: c => c.Select(x => x.Seq == 2 ? x with { Root = new string('1', 64) } : x).ToList());
        Assert.False(root.Valid);

        // Proof against an anchor that is not on the chain.
        var (offchain, _, _) = await VerifyAsync(a, chainTamper: c => c.Where(x => x.Seq != 3).ToList());
        Assert.False(offchain.Valid);

        // The untouched export passes.
        var (ok, _, _) = await VerifyAsync(a);
        Assert.True(ok.Valid, string.Join("; ", ok.Errors));
    }

    [Fact]
    public async Task Verifier_RejectsAFirstInclusionMoreThanTwoAnchorsAfterTheRecordedOne()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var db = NewContext();
        var w = Writer(db);
        await w.AppendFirstAsync(b, "message", P("b1"));
        for (var i = 0; i < 4; i++) { await Service(db).TickAsync(); _time.Advance(TimeSpan.FromMinutes(5)); }
        await w.AppendFirstAsync(a, "message", P("a1")); // records anchor 4
        for (var i = 0; i < 4; i++) { await Service(db).TickAsync(); _time.Advance(TimeSpan.FromMinutes(5)); }

        var (ok, _, ex) = await VerifyAsync(a);
        Assert.True(ok.Valid, string.Join("; ", ok.Errors));
        Assert.Equal(5, ex.Proofs[0].AnchorSeq);

        // Drop the first two proofs: the first inclusion would then be anchor 7, beyond the bound.
        var (late, _, _) = await VerifyAsync(a, e => e with { Proofs = e.Proofs.Skip(2).ToList() });
        Assert.False(late.Valid);
    }

    private sealed class FailAnchorInserts : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public volatile bool Fail;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Fail && eventData.Context!.ChangeTracker.Entries<EvidenceAnchor>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("simulated persist failure");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task PersistFailureAfterBroadcast_IsCompletedByTheNextTick_WithoutDuplicateSequenceNumbers()
    {
        var a = Guid.NewGuid();
        var gate = new FailAnchorInserts { Fail = true };
        await using (var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).AddInterceptors(gate).Options))
        {
            await Writer(db).AppendFirstAsync(a, "message", P("a1"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).TickAsync());
            Assert.Empty(db.ChangeTracker.Entries<EvidenceAnchor>()); // half-added entities are not kept
        }
        gate.Fail = false;

        // "Restart": a fresh context. The transaction is on chain (seq 1); the next tick stores its anchor and proofs first.
        _time.Advance(TimeSpan.FromMinutes(10));
        await using var db2 = NewContext();
        var next = await Service(db2).TickAsync();
        var anchors = await db2.EvidenceAnchors.AsNoTracking().OrderBy(x => x.SequenceNumber).ToListAsync();
        Assert.Equal([1L, 2L], anchors.Select(x => x.SequenceNumber).ToArray());
        Assert.Equal(2, next.Anchor!.SequenceNumber);
        Assert.Equal(anchors[0].AnchorTxId, next.Anchor.PreviousAnchorTxId);
        Assert.Null(next.Anchor.GapFrom);
        Assert.Equal(1, await db2.EvidenceAnchorLeaves.CountAsync(l => l.AnchorId == anchors[0].Id));
        Assert.Empty(await db2.EvidenceAnchorAttempts.ToListAsync());
        Assert.Equal(2, _chain.Notes.Count);
        var (v, _, _) = await VerifyAsync(a);
        Assert.True(v.Valid, string.Join("; ", v.Errors));
    }

    [Fact]
    public async Task LostTick_SurvivesARestart()
    {
        var a = Guid.NewGuid();
        await using (var db = NewContext())
        {
            await Writer(db).AppendFirstAsync(a, "message", P("a1"));
            await Service(db).TickAsync();           // seq 1
            _chain.FailNext = 2;
            _time.Advance(TimeSpan.FromMinutes(10));
            Assert.True((await Service(db).TickAsync()).Lost); // seq 2 lost
        }
        _time.Advance(TimeSpan.FromMinutes(10));
        await using var db2 = NewContext();          // new process: no in-memory state
        var next = await Service(db2).TickAsync();
        Assert.Equal(3, next.Anchor!.SequenceNumber);
        Assert.Equal("2", next.Anchor.GapFrom);
        var (v, _, _) = await VerifyAsync(a);
        Assert.True(v.Valid, string.Join("; ", v.Errors));
    }

    [Fact]
    public async Task UnconfirmedButStillValid_IsNotLost_BlocksNewAnchors_AndCompletesWhenItLands()
    {
        var a = Guid.NewGuid();
        await using var db = NewContext();
        await Writer(db).AppendFirstAsync(a, "message", P("a1"));
        await Service(db).TickAsync();                 // seq 1
        _chain.FailNext = 2;
        _chain.Resolution = new AnchorResolution(AnchorResolutionKind.Pending);
        _time.Advance(TimeSpan.FromMinutes(10));
        var r = await Service(db).TickAsync();          // seq 2 not confirmed in time, but still valid
        Assert.False(r.Lost);
        Assert.Null(r.Anchor);

        _time.Advance(TimeSpan.FromMinutes(1));
        var calls = _chain.Calls;
        Assert.Null((await Service(db).TickAsync()).Anchor);   // still pending: no second anchor is started
        Assert.Equal(calls, _chain.Calls);

        _chain.Resolution = new AnchorResolution(AnchorResolutionKind.Confirmed, "TXLANDED");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Service(db).TickAsync();                  // completes seq 2 from the attempt row
        await using var check = NewContext();
        var two = await check.EvidenceAnchors.SingleAsync(x => x.SequenceNumber == 2);
        Assert.Equal("TXLANDED", two.AnchorTxId);
        Assert.Null(two.GapFrom);
        Assert.Empty(await check.EvidenceAnchorAttempts.ToListAsync());
    }

    [Fact]
    public async Task UnconfirmedAndExpired_IsLost_AndTheNextAnchorRecordsTheGap()
    {
        var a = Guid.NewGuid();
        await using var db = NewContext();
        await Writer(db).AppendFirstAsync(a, "message", P("a1"));
        await Service(db).TickAsync();
        _chain.FailNext = 2;
        _chain.Resolution = new AnchorResolution(AnchorResolutionKind.Pending);
        _time.Advance(TimeSpan.FromMinutes(10));
        await Service(db).TickAsync();                  // pending
        _chain.Resolution = new AnchorResolution(AnchorResolutionKind.Expired);
        _time.Advance(TimeSpan.FromMinutes(1));
        var next = await Service(db).TickAsync();
        Assert.Equal(3, next.Anchor!.SequenceNumber);
        Assert.Equal("2", next.Anchor.GapFrom);
    }

    [Fact]
    public async Task UnknownToTheBroadcaster_StaysPendingUntilTheGraceHasPassed()
    {
        var a = Guid.NewGuid();
        await using var db = NewContext();
        await Writer(db).AppendFirstAsync(a, "message", P("a1"));
        _chain.FailNext = 2;
        _chain.Resolution = new AnchorResolution(AnchorResolutionKind.Unknown);
        await Service(db).TickAsync();                  // seq 1 unconfirmed; the broadcaster forgot it (restart)
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await Service(db).TickAsync()).Anchor);
        _time.Advance(TimeSpan.FromMinutes(10));        // past UnknownGrace
        var next = await Service(db).TickAsync();
        Assert.Equal(2, next.Anchor!.SequenceNumber);
    }

    [Fact]
    public async Task CrashBeforeTheTxIdIsKnown_ConsumesTheSequenceNumberAndRecordsTheGap()
    {
        var a = Guid.NewGuid();
        await using var db = NewContext();
        await Writer(db).AppendFirstAsync(a, "message", P("a1"));
        await Service(db).TickAsync();               // seq 1
        db.EvidenceAnchorAttempts.Add(new EvidenceAnchorAttempt { SequenceNumber = 2, Status = AnchorService.PendingStatus, MerkleRoot = new string('0', 64), CreatedAt = _time.GetUtcNow() });
        await db.SaveChangesAsync();                 // a process died after this write
        _time.Advance(TimeSpan.FromMinutes(10));
        await using var db2 = NewContext();
        var next = await Service(db2).TickAsync();
        Assert.Equal(3, next.Anchor!.SequenceNumber);
        Assert.Equal("2", next.Anchor.GapFrom);
    }
}

/// <summary>A fake Algod that serves just what the broadcaster calls, and records every submit.</summary>
internal sealed class FakeAlgod : HttpMessageHandler
{
    public ulong LastRound = 1000;
    public bool ConfirmAll;                          // pending lookups report a confirmed round
    public bool FailSubmitAfterAccepting;            // the node takes the tx, then the HTTP call errors
    public bool IndexerConfirms;                     // the indexer knows the tx as confirmed while the pending endpoint 404s
    public List<byte[]> Submitted { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/v2/transactions/params"))
            return Json("{\"consensus-version\":\"future\",\"fee\":0,\"genesis-hash\":\"" + Convert.ToBase64String(new byte[32]) + "\",\"genesis-id\":\"testnet-v1.0\",\"last-round\":" + LastRound + ",\"min-fee\":1000}");
        if (req.Method == HttpMethod.Post && path.EndsWith("/v2/transactions"))
        {
            Submitted.Add(await req.Content!.ReadAsByteArrayAsync(ct));
            if (FailSubmitAfterAccepting) return new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);
            return Json("{\"txId\":\"X\"}");
        }
        if (req.RequestUri.Host == "indexer.test")
            return IndexerConfirms ? Json("{\"transaction\":{\"confirmed-round\":1001},\"current-round\":1002}") : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        if (path.Contains("/v2/transactions/pending/"))
            return ConfirmAll ? Json("{\"confirmed-round\":1001}") : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string j) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(j, Encoding.UTF8, "application/json") };
}

public class AlgodAnchorBroadcasterTests
{
    private static readonly byte[] Note = "{\"root\":\"x\",\"seq\":1,\"v\":1}"u8.ToArray();

    private static AlgodAnchorBroadcaster Make(FakeAlgod algod, bool indexer = false)
    {
        var mnemonic = new Algorand.Algod.Model.Account().ToMnemonic();
        var o = Options.Create(new AnchoringOptions { AccountMnemonic = mnemonic, Network = "testnet", IndexerUrl = indexer ? "http://indexer.test/" : null, ConfirmTimeout = TimeSpan.FromMilliseconds(30), ConfirmPollInterval = TimeSpan.FromMilliseconds(5) });
        var a = Options.Create(new Biatec.Chat.Api.Config.AlgodOptions { Networks = { ["testnet"] = new() { Url = "http://algod.test/" } } });
        return new AlgodAnchorBroadcaster(o, a, algod);
    }

    private static bool Contains(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task SubmitsOneSelfPaymentCarryingTheNote_AndReturnsOnceConfirmed()
    {
        var algod = new FakeAlgod { ConfirmAll = true };
        var txId = await Make(algod).BroadcastAsync(Note, default);
        Assert.False(string.IsNullOrEmpty(txId));
        var body = Assert.Single(algod.Submitted);
        Assert.True(Contains(body, Note), "the signed transaction carries the note bytes");
        Assert.True(Contains(body, "pay"u8.ToArray()));
        Assert.True(Contains(body, "testnet-v1.0"u8.ToArray()));
    }

    [Fact]
    public async Task TimeoutAfterSubmit_RetryDoesNotSendASecondTransaction_ItFindsTheFirstConfirmed()
    {
        var algod = new FakeAlgod();
        var b = Make(algod);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default)); // submitted, not seen confirmed in time
        Assert.Single(algod.Submitted);

        algod.ConfirmAll = true;                       // it landed after all
        var txId = await b.BroadcastAsync(Note, default);
        Assert.Single(algod.Submitted);                // no second transaction for the same note
        Assert.False(string.IsNullOrEmpty(txId));
    }

    [Fact]
    public async Task HttpErrorAfterTheNodeAcceptedTheTx_RetryReusesTheSameSignedTransaction()
    {
        var algod = new FakeAlgod { FailSubmitAfterAccepting = true };
        var b = Make(algod);
        await Assert.ThrowsAnyAsync<Exception>(() => b.BroadcastAsync(Note, default));
        algod.FailSubmitAfterAccepting = false;
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default)); // still valid: same bytes re-submitted
        Assert.True(algod.Submitted.Count >= 2);
        Assert.All(algod.Submitted, x => Assert.Equal(algod.Submitted[0], x));
    }

    [Fact]
    public async Task SendsANewTransactionOnlyAfterTheFirstCanNoLongerConfirm()
    {
        var algod = new FakeAlgod();
        var b = Make(algod);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        algod.LastRound += 5000;                       // LastValid has passed and it never confirmed
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default)); // expired: a fresh transaction is sent
        Assert.Equal(2, algod.Submitted.Count);
        Assert.NotEqual(algod.Submitted[0], algod.Submitted[1]);
    }

    [Fact]
    public async Task Resolve_ReportsPending_ThenExpired_AndForgetsTheNote()
    {
        var algod = new FakeAlgod();
        var b = Make(algod);
        Assert.Equal(AnchorResolutionKind.Unknown, (await b.ResolveAsync(Note, default)).Kind);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        Assert.Equal(AnchorResolutionKind.Pending, (await b.ResolveAsync(Note, default)).Kind);
        algod.LastRound += 100;                         // past LastValid (+40)
        Assert.Equal(AnchorResolutionKind.Expired, (await b.ResolveAsync(Note, default)).Kind);
        Assert.Equal(AnchorResolutionKind.Unknown, (await b.ResolveAsync(Note, default)).Kind); // entry dropped, no leak
    }

    [Fact]
    public async Task PendingEndpoint404_ButIndexerReportsConfirmed_IsConfirmed_NotExpired()
    {
        var algod = new FakeAlgod();
        var b = Make(algod, indexer: true);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        algod.IndexerConfirms = true;
        algod.LastRound += 5000;                        // past LastValid: without the indexer this would be Expired
        var r = await b.ResolveAsync(Note, default);
        Assert.Equal(AnchorResolutionKind.Confirmed, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.TxId));
    }

    [Fact]
    public async Task Broadcast_RetryFindsTheTxInTheIndexer_AndSendsNoSecondTransaction()
    {
        var algod = new FakeAlgod();
        var b = Make(algod, indexer: true);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        algod.IndexerConfirms = true;
        algod.LastRound += 5000;
        Assert.False(string.IsNullOrEmpty(await b.BroadcastAsync(Note, default)));
        Assert.Single(algod.Submitted);
    }

    [Fact]
    public async Task Resolve_404FromBothAlgodAndIndexer_IsNotLanded()
    {
        var algod = new FakeAlgod();
        var b = Make(algod, indexer: true);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        algod.LastRound += 5000;
        Assert.Equal(AnchorResolutionKind.Expired, (await b.ResolveAsync(Note, default)).Kind);
    }

    [Fact]
    public async Task Resolve_FindsATransactionThatConfirmedAfterTheTimeout()
    {
        var algod = new FakeAlgod();
        var b = Make(algod);
        await Assert.ThrowsAsync<TimeoutException>(() => b.BroadcastAsync(Note, default));
        algod.ConfirmAll = true;
        var r = await b.ResolveAsync(Note, default);
        Assert.Equal(AnchorResolutionKind.Confirmed, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.TxId));
    }
}
