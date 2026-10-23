using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Api.Wallet.Transfer;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.Wallet.Bitcoin;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Tests.Bitcoin;

/// <summary>Builds unsigned PSBTs (BIP-174 v0) for tests. Independent of the decoder under test.</summary>
internal static class PsbtFixture
{
    public static byte[] P2wpkh(string hash160Hex) => [0x00, 0x14, .. Convert.FromHexString(hash160Hex)];
    public const string H1 = "751e76e8199196d454941c45d1b3a323f1433bd6"; // bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4
    public const string H2 = "1d0f172a0ecb48aee1be1f2687d2963ae33f71a1"; // bc1qr583w2swedy2acd7rung055k8t3n7udp7vyzyg
    public const string H3 = "00112233445566778899aabbccddeeff00112233";
    public const string A1 = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    public const string A2 = "bc1qr583w2swedy2acd7rung055k8t3n7udp7vyzyg";

    public sealed record In(string Txid, uint Vout, long Sats, byte[] Script, uint Sequence = 0xfffffffd, byte[]? PrevTx = null);

    private static byte[] Cs(int n) => n < 253 ? [(byte)n] : [0xfd, (byte)n, (byte)(n >> 8)];
    private static byte[] Le32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] Le64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); return b; }
    private static byte[] Kv(byte[] k, byte[] v) => [.. Cs(k.Length), .. k, .. Cs(v.Length), .. v];

    /// <summary>A transaction with one dummy input and the given outputs; returns its bytes and txid (display order).</summary>
    public static (byte[] Bytes, string Txid) FundingTx(params (byte[] Script, long Sats)[] outs)
    {
        var raw = new List<byte>();
        raw.AddRange(Le32(2)); raw.AddRange(Cs(1));
        raw.AddRange(new byte[32]); raw.AddRange(Le32(0)); raw.AddRange(Cs(0)); raw.AddRange(Le32(0xffffffff));
        raw.AddRange(Cs(outs.Length));
        foreach (var (s, v) in outs) { raw.AddRange(Le64(v)); raw.AddRange(Cs(s.Length)); raw.AddRange(s); }
        raw.AddRange(Le32(0));
        var bytes = raw.ToArray();
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(SHA256.HashData(bytes)).Reverse().ToArray()));
    }

    public static string Build(IReadOnlyList<In> ins, IReadOnlyList<(byte[] Script, long Sats)> outs, int version = 2, uint locktime = 0)
    {
        var raw = new List<byte>();
        raw.AddRange(Le32((uint)version)); raw.AddRange(Cs(ins.Count));
        foreach (var i in ins) { raw.AddRange(Convert.FromHexString(i.Txid).Reverse()); raw.AddRange(Le32(i.Vout)); raw.AddRange(Cs(0)); raw.AddRange(Le32(i.Sequence)); }
        raw.AddRange(Cs(outs.Count));
        foreach (var (s, v) in outs) { raw.AddRange(Le64(v)); raw.AddRange(Cs(s.Length)); raw.AddRange(s); }
        raw.AddRange(Le32(locktime));

        var psbt = new List<byte> { 0x70, 0x73, 0x62, 0x74, 0xff };
        psbt.AddRange(Kv([0x00], raw.ToArray())); psbt.Add(0);
        foreach (var i in ins)
        {
            psbt.AddRange(i.PrevTx is null
                ? Kv([0x01], [.. Le64(i.Sats), .. Cs(i.Script.Length), .. i.Script])
                : Kv([0x00], i.PrevTx));
            psbt.Add(0);
        }
        foreach (var _ in outs) psbt.Add(0);
        return Convert.ToBase64String(psbt.ToArray());
    }

    /// <summary>The standard transfer: one P2WPKH input from A1, 100000 sats to A2, change to A1, 1000 sats fee.</summary>
    public static string Standard(string txid = "c37af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb670", long inSats = 150000, long pay = 100000, long fee = 1000)
        => Build([new In(txid, 1, inSats, P2wpkh(H1))], [(P2wpkh(H2), pay), (P2wpkh(H1), inSats - pay - fee)]);
}

public sealed class BitcoinEncodingTests
{
    // The fixed test vector: this PSBT is the one in PRODUCT-DESCRIPTION.md's intent format; the hash was computed
    // independently with Python (hashlib over json.dumps(sort_keys, compact)), not with the code under test.
    private const string VectorPsbtHex =
        "70736274ff010071020000000170b68c4749ebd05776915b4d01297947f182ace3e9aa68af7cb2d11611f37ac30100000000fdffffff02a0860100000000001600141d0f172a0ecb48aee1be1f2687d2963ae33f71a168bf000000000000160014751e76e8199196d454941c45d1b3a323f1433bd6000000000001011ff049020000000000160014751e76e8199196d454941c45d1b3a323f1433bd6000000";
    private const string VectorCanonical =
        "{\"feeSats\":1000,\"inputs\":[{\"sats\":150000,\"scriptPubKey\":\"0014751e76e8199196d454941c45d1b3a323f1433bd6\",\"sequence\":4294967293,\"txid\":\"c37af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb670\",\"vout\":1}],\"locktime\":0,\"outputs\":[{\"address\":\"bc1qr583w2swedy2acd7rung055k8t3n7udp7vyzyg\",\"sats\":100000,\"scriptPubKey\":\"00141d0f172a0ecb48aee1be1f2687d2963ae33f71a1\"},{\"address\":\"bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4\",\"sats\":49000,\"scriptPubKey\":\"0014751e76e8199196d454941c45d1b3a323f1433bd6\"}],\"version\":2}";
    private const string VectorHash = "5f6c9c861c658b2c88e6ed0fc1555b6323f8d149a32efea4406f74999611aed9";

    [Fact]
    public void Intent_hash_matches_the_fixed_vector()
    {
        var intent = PsbtDecoder.Decode(VectorPsbtHex, testnet: false);
        Assert.Equal(VectorHash, intent.Hash());
        Assert.Equal(VectorCanonical, Biatec.Chat.Core.Canonical.JsonCanonicalizer.Canonicalize(intent.ToJson()));
        Assert.Equal(1000, intent.FeeSats);
        Assert.Equal(2, intent.Version);
    }

    [Fact]
    public void The_same_transaction_as_base64_has_the_same_hash()
    {
        var b64 = Convert.ToBase64String(Convert.FromHexString(VectorPsbtHex));
        Assert.Equal(VectorHash, PsbtDecoder.Decode(b64, false).Hash());
    }

    [Fact]
    public void Hash_changes_when_a_committed_field_changes()
    {
        var baseline = PsbtDecoder.Decode(PsbtFixture.Standard(), false).Hash();
        var ins = new[] { new PsbtFixture.In("c37af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb670", 1, 150000, PsbtFixture.P2wpkh(PsbtFixture.H1)) };
        var outs = new[] { (PsbtFixture.P2wpkh(PsbtFixture.H2), 100000L), (PsbtFixture.P2wpkh(PsbtFixture.H1), 49000L) };
        Assert.Equal(baseline, PsbtDecoder.Decode(PsbtFixture.Build(ins, outs), false).Hash());
        Assert.NotEqual(baseline, PsbtDecoder.Decode(PsbtFixture.Build(ins, outs, version: 1), false).Hash());
        Assert.NotEqual(baseline, PsbtDecoder.Decode(PsbtFixture.Build(ins, outs, locktime: 5), false).Hash());
        Assert.NotEqual(baseline, PsbtDecoder.Decode(PsbtFixture.Build([ins[0] with { Sequence = 0xffffffff }], outs), false).Hash());
        Assert.NotEqual(baseline, PsbtDecoder.Decode(PsbtFixture.Build(ins, [(PsbtFixture.P2wpkh(PsbtFixture.H3), 100000L), outs[1]]), false).Hash());
    }

    [Fact]
    public void Decodes_a_legacy_input_from_non_witness_utxo_and_checks_its_txid()
    {
        byte[] p2pkh = [0x76, 0xa9, 0x14, .. Convert.FromHexString(PsbtFixture.H1), 0x88, 0xac];
        var (prev, prevTxid) = PsbtFixture.FundingTx((PsbtFixture.P2wpkh(PsbtFixture.H3), 5), (p2pkh, 70000));
        var good = PsbtFixture.Build([new PsbtFixture.In(prevTxid, 1, 0, p2pkh, PrevTx: prev)], [(PsbtFixture.P2wpkh(PsbtFixture.H2), 60000)]);
        var intent = PsbtDecoder.Decode(good, false);
        Assert.Equal(70000, intent.Inputs[0].Sats);
        Assert.Equal(10000, intent.FeeSats);
        Assert.StartsWith("1", intent.Inputs[0].Address);

        var wrongTxid = PsbtFixture.Build([new PsbtFixture.In(new string('1', 64), 1, 0, p2pkh, PrevTx: prev)], [(PsbtFixture.P2wpkh(PsbtFixture.H2), 60000)]);
        Assert.Throws<PsbtException>(() => PsbtDecoder.Decode(wrongTxid, false));
    }

    [Fact]
    public void Refuses_a_psbt_that_cannot_be_shown_honestly()
    {
        var noPrevout = Convert.ToHexStringLower(Convert.FromBase64String(PsbtFixture.Standard()));
        // Drop the witness_utxo record (key 01 01 ...): replace it by an empty input map.
        var stripped = noPrevout.Replace("01011ff049020000000000160014751e76e8199196d454941c45d1b3a323f1433bd600", "");
        Assert.Throws<PsbtException>(() => PsbtDecoder.Decode(stripped, false));
        Assert.Throws<PsbtException>(() => PsbtDecoder.Decode(noPrevout[..60], false));
        Assert.Throws<PsbtException>(() => PsbtDecoder.Decode("not a psbt", false));
        Assert.Throws<PsbtException>(() => PsbtDecoder.Decode(PsbtFixture.Build([new PsbtFixture.In(new string('a', 64), 0, 100, PsbtFixture.P2wpkh(PsbtFixture.H1))], [(PsbtFixture.P2wpkh(PsbtFixture.H2), 500)]), false));
    }

    [Theory]
    [InlineData("0014751e76e8199196d454941c45d1b3a323f1433bd6", "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", false)]
    [InlineData("0014751e76e8199196d454941c45d1b3a323f1433bd6", "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", true)]
    [InlineData("00201863143c14c5166804bd19203356da136c985678cd4d27a1b8c6329604903262", "bc1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3qccfmv3", false)]
    [InlineData("512079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0", false)]
    [InlineData("76a914000000000000000000000000000000000000000088ac", "1111111111111111111114oLvT2", false)]
    public void Addresses_are_derived_from_scripts(string script, string address, bool testnet) =>
        Assert.Equal(address, BitcoinAddresses.FromScript(Convert.FromHexString(script), testnet));

    [Fact]
    public void An_op_return_output_has_no_address() =>
        Assert.Null(BitcoinAddresses.FromScript(Convert.FromHexString("6a0568656c6c6f"), false));
}

public sealed class BitcoinTransferTests : IClassFixture<BitcoinTransferTests.DbFixture>, IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private const string Utxo1 = "c37af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb670";
    private const string Utxo2 = "d47af31116d1b27caf68aae9e3ac82f1477929014d5b917657d0eb49478cb671";

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWatcher : IBitcoinChainWatcher
    {
        public HashSet<string> Spent { get; } = [];
        public decimal? FeeRate { get; set; }
        /// <summary>What the chain says about a transaction's outputs; the default answers from the PSBT the test serves (so a test that does not care about this passes).</summary>
        public Func<string, IReadOnlyList<BitcoinPrevout>?>? Outputs { get; set; }
        public Task<IReadOnlyList<BitcoinPrevout>?> GetOutputsAsync(string network, string txid, CancellationToken ct) => Task.FromResult(Outputs?.Invoke(txid));
        public Task<bool> IsSpentAsync(string network, string txid, uint vout, CancellationToken ct) => Task.FromResult(Spent.Contains($"{txid}:{vout}"));
        public Task<decimal?> GetFeeRateAsync(string network, CancellationToken ct) => Task.FromResult(FeeRate);
    }

    /// <summary>A Biatec MCP stand-in that returns the PSBT it is told to.</summary>
    private sealed class FakeMcp : IBiatecMcpClient
    {
        public Func<string> Psbt { get; set; } = () => PsbtFixture.Standard();
        public string[] Own { get; set; } = [PsbtFixture.A1];
        public List<string> Calls { get; } = [];

        private static McpToolResult Ok(string json) => new(false, json, JsonDocument.Parse(json).RootElement.Clone());

        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default)
        {
            Calls.Add("getCryptoAddress:" + network);
            return Task.FromResult(Ok(JsonSerializer.Serialize(new { addresses = Own })));
        }

        /// <summary>The slot-1 (secondary) address; null behaves like a client that cannot pass a slot.</summary>
        public string? Secondary { get; set; }

        public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, int slot, CancellationToken ct = default)
        {
            if (slot == 0) return GetCryptoAddressAsync(ctx, network, ct);
            if (Secondary is null) throw new NotSupportedException();
            Calls.Add($"getCryptoAddress:{network}:{slot}");
            return Task.FromResult(Ok(JsonSerializer.Serialize(new { network, family = "Btc", address = Secondary, error = "" })));
        }

        public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default)
        {
            Calls.Add($"createBitcoinTransaction:{network}:{sender[..6]}>{receiver[..6]}:{amountBtc}");
            return Task.FromResult(Ok(JsonSerializer.Serialize(new { psbt = Psbt() })));
        }

        public Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public sealed class DbFixture : IAsyncLifetime
    {
        private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
        public string ConnectionString { get; private set; } = "";

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
            ConnectionString = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
            await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(ConnectionString).Options);
            await db.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMcp _mcp = new();
    private readonly FakeWatcher _watcher = new();
    private readonly string _cs;
    private Guid _conversation;

    public BitcoinTransferTests(DbFixture fixture) => _cs = fixture.ConnectionString;

    private ChatDbContext NewContext() => new(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(_cs).Options);

    public async Task InitializeAsync()
    {
        // By default the "chain" agrees with whatever PSBT the test serves; the cross-check tests override this.
        _watcher.Outputs = txid => ChainFrom(_mcp.Psbt(), txid, 0);
        _conversation = Guid.NewGuid();
        await using var db = NewContext();
        db.Conversations.Add(new Conversation { Id = _conversation, TenantId = "t1", CreatedAt = _clock.GetUtcNow(), LastMessageAt = _clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static McpCallContext Ctx => new("t1", "sess", ["openid"]);
    private static BitcoinTransferRequest Req => new("bitcoin", PsbtFixture.A1, PsbtFixture.A2, 100000);

    private (BitcoinTransferService Service, ChatDbContext Db) NewService(bool withWatcher = true)
    {
        var db = NewContext();
        return (new BitcoinTransferService(_mcp, new ActionPreviewService(db, _clock), db, _clock, withWatcher ? _watcher : null), db);
    }

    [Fact]
    public async Task Builds_through_mcp_and_binds_the_preview_to_the_intent_hash()
    {
        var (svc, _) = NewService();
        var outcome = await svc.StartAsync(Ctx, _conversation, Req, default);

        Assert.False(outcome.IsError, outcome.Message);
        Assert.Equal(["getCryptoAddress:bitcoin", "createBitcoinTransaction:bitcoin:bc1qw5>bc1qr5:0.001"], _mcp.Calls);
        var step = Assert.Single(outcome.Snapshot!.Steps);
        Assert.Equal(PsbtDecoder.Decode(PsbtFixture.Standard(), false).Hash(), step.SubjectHash);
        Assert.Equal(PsbtFixture.A1, step.SenderAddress);
        Assert.Equal("bitcoin", step.Network);
        Assert.Equal(_clock.Now.AddMinutes(30), step.ExpiresAt);
        Assert.Equal(_clock.Now.AddMinutes(30), outcome.Snapshot.Preview.ExpiresAt);
        Assert.Equal(ActionStepKind.Sign, step.Kind);
    }

    [Fact]
    public async Task The_card_shows_inputs_outputs_change_and_fee_rate()
    {
        var (svc, _) = NewService();
        var card = BitcoinTransferCard.Map(await svc.StartAsync(Ctx, _conversation, Req, default));

        Assert.Equal("wallet.transfer", card.Capability);
        Assert.Equal("bitcoin", card.Network);
        Assert.Contains("0.001 BTC", card.Intent);
        var step = Assert.Single(card.Steps);
        Assert.Equal("1000", step.FeeAmount);
        Assert.Equal(64, step.GroupHash.Length);
        Assert.Contains("Inputs: bc1qw5...v8f3t4 0.0015 (c37af3...8cb670:1)", step.SimulationSummary);
        Assert.Contains("bc1qr5...7vyzyg 0.001", step.SimulationSummary);
        Assert.Contains("bc1qw5...v8f3t4 0.00049 (change)", step.SimulationSummary);
        Assert.Contains("Change: 0.00049 BTC", step.SimulationSummary);
        Assert.Contains("7.12 sat/vB", step.SimulationSummary);
        Assert.Equal("100000", step.Assets[0].Amount);
        Assert.Equal(8, step.Assets[0].Decimals);
    }

    [Fact]
    public async Task A_spent_input_invalidates_the_preview_and_the_next_call_rebuilds_it()
    {
        var (svc, db) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        var id = first.Snapshot!.Preview.Id;
        Assert.Equal(BitcoinInvalidation.None, await svc.CheckAsync(_conversation, id, default));

        _watcher.Spent.Add($"{Utxo1}:1");
        Assert.Equal(BitcoinInvalidation.InputSpent, await svc.CheckAsync(_conversation, id, default));
        await using (var check = NewContext())
        {
            Assert.Equal(ActionPreviewStatus.Expired, (await check.ActionPreviews.SingleAsync(p => p.Id == id)).Status);
            Assert.All(await check.ActionSteps.Where(s => s.PreviewId == id).ToListAsync(), s => Assert.Equal(StepStatus.Expired, s.Status));
        }
        // An expired preview cannot be confirmed.
        var snap = await new ActionPreviewService(NewContext(), _clock).GetAsync(_conversation, id);
        Assert.False(snap.Countdown.CanConfirm);

        _mcp.Psbt = () => PsbtFixture.Standard(txid: Utxo2);
        var rebuilt = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.False(rebuilt.IsError, rebuilt.Message);
        Assert.NotEqual(id, rebuilt.Snapshot!.Preview.Id);
        Assert.NotEqual(first.Snapshot.Steps[0].SubjectHash, rebuilt.Snapshot.Steps[0].SubjectHash);
        Assert.Equal(ActionPreviewStatus.Proposed, rebuilt.Snapshot.Preview.Status);
        _ = db;
    }

    [Fact]
    public async Task A_second_identical_request_reuses_the_valid_preview()
    {
        var (svc, _) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        var again = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.Equal(first.Snapshot!.Preview.Id, again.Snapshot!.Preview.Id);
        Assert.Equal(2, _mcp.Calls.Count); // not built again
    }

    [Fact]
    public async Task The_preview_lapses_after_30_minutes()
    {
        var (svc, _) = NewService(withWatcher: false);
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        _clock.Now = _clock.Now.AddMinutes(29);
        Assert.Equal(BitcoinInvalidation.None, await svc.CheckAsync(_conversation, first.Snapshot!.Preview.Id, default));
        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Equal(BitcoinInvalidation.TimeExpired, await svc.CheckAsync(_conversation, first.Snapshot.Preview.Id, default));
    }

    [Theory]
    [InlineData("8.0", false)]   // +12 percent
    [InlineData("5.6", false)]   // -21 percent
    [InlineData("9.3", true)]    // +30 percent
    [InlineData("5.0", true)]    // -30 percent
    public async Task A_fee_rate_move_over_25_percent_invalidates(string rate, bool invalid)
    {
        var (svc, _) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        _watcher.FeeRate = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);
        var result = await svc.CheckAsync(_conversation, first.Snapshot!.Preview.Id, default);
        Assert.Equal(invalid ? BitcoinInvalidation.FeeRateMoved : BitcoinInvalidation.None, result);
    }

    [Fact]
    public async Task Refuses_inputs_from_more_than_one_address()
    {
        var h3 = PsbtFixture.P2wpkh(PsbtFixture.H3);
        _mcp.Psbt = () => PsbtFixture.Build(
            [new PsbtFixture.In(Utxo1, 0, 80000, PsbtFixture.P2wpkh(PsbtFixture.H1)), new PsbtFixture.In(Utxo2, 0, 80000, h3)],
            [(PsbtFixture.P2wpkh(PsbtFixture.H2), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H1), 59000)]);
        var (svc, db) = NewService();
        var outcome = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.True(outcome.IsError);
        Assert.Contains("one sender address", outcome.Message);
        Assert.Empty(await db.ActionPreviews.Where(p => p.ConversationId == _conversation).ToListAsync());
    }

    [Fact]
    public async Task Refuses_a_transaction_that_does_not_match_the_request()
    {
        var (svc, db) = NewService();
        // Wrong amount.
        _mcp.Psbt = () => PsbtFixture.Standard(pay: 90000);
        Assert.True((await svc.StartAsync(Ctx, _conversation, Req, default)).IsError);
        // Extra output to a third party.
        _mcp.Psbt = () => PsbtFixture.Build([new PsbtFixture.In(Utxo1, 1, 150000, PsbtFixture.P2wpkh(PsbtFixture.H1))],
            [(PsbtFixture.P2wpkh(PsbtFixture.H2), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H3), 49000)]);
        var stolen = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.True(stolen.IsError);
        Assert.Contains("other than the payment and change", stolen.Message);
        // Absurd fee.
        _mcp.Psbt = () => PsbtFixture.Standard(inSats: 400000, fee: 290000);
        Assert.True((await svc.StartAsync(Ctx, _conversation, Req, default)).IsError);
        // Not a PSBT.
        _mcp.Psbt = () => "hello";
        Assert.True((await svc.StartAsync(Ctx, _conversation, Req, default)).IsError);
        Assert.Empty(await db.ActionPreviews.Where(p => p.ConversationId == _conversation).ToListAsync());
    }

    [Fact]
    public async Task A_self_transfer_refuses_a_second_output_to_a_third_party()
    {
        var self = new BitcoinTransferRequest("bitcoin", PsbtFixture.A1, PsbtFixture.A1, 100000);
        var (svc, db) = NewService();
        _mcp.Psbt = () => PsbtFixture.Build([new PsbtFixture.In(Utxo1, 1, 150000, PsbtFixture.P2wpkh(PsbtFixture.H1))],
            [(PsbtFixture.P2wpkh(PsbtFixture.H1), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H3), 49000)]);
        var bad = await svc.StartAsync(Ctx, _conversation, self, default);
        Assert.True(bad.IsError);
        Assert.Contains("other than the payment and change", bad.Message);
        Assert.Empty(await db.ActionPreviews.Where(p => p.ConversationId == _conversation).ToListAsync());

        _mcp.Psbt = () => PsbtFixture.Build([new PsbtFixture.In(Utxo1, 1, 150000, PsbtFixture.P2wpkh(PsbtFixture.H1))],
            [(PsbtFixture.P2wpkh(PsbtFixture.H1), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H1), 49000)]);
        Assert.False((await svc.StartAsync(Ctx, _conversation, self, default)).IsError);
    }

    [Fact]
    public async Task A_completed_transfer_can_be_repeated_with_a_new_preview()
    {
        var (svc, _) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        var id = first.Snapshot!.Preview.Id;
        await using (var db = NewContext())
        {
            await db.ActionSteps.Where(s => s.PreviewId == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatus.Confirmed));
            await db.ActionPreviews.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Completed));
        }
        _mcp.Psbt = () => PsbtFixture.Standard(txid: Utxo2);
        var (svc2, _) = NewService();
        var again = await svc2.StartAsync(Ctx, _conversation, Req, default);
        Assert.False(again.IsError, again.Message);
        Assert.NotEqual(id, again.Snapshot!.Preview.Id);
        Assert.Equal(ActionPreviewStatus.Proposed, again.Snapshot.Preview.Status);
    }

    [Fact]
    public async Task A_transfer_in_flight_is_not_started_twice()
    {
        var (svc, _) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        var id = first.Snapshot!.Preview.Id;
        await using (var db = NewContext())
        {
            await db.ActionSteps.Where(s => s.PreviewId == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatus.Submitted));
            await db.ActionPreviews.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.InProgress));
        }
        var (svc2, _) = NewService();
        var again = await svc2.StartAsync(Ctx, _conversation, Req, default);
        Assert.Equal(id, again.Snapshot!.Preview.Id);
        Assert.Contains("being signed", again.Message);
    }

    [Fact]
    public async Task The_confirm_gate_refuses_a_preview_whose_input_was_spent()
    {
        var (svc, _) = NewService();
        var first = await svc.StartAsync(Ctx, _conversation, Req, default);
        var id = first.Snapshot!.Preview.Id;
        var gate = new BitcoinPreviewFreshnessGate(svc);
        await using var db = NewContext();
        var preview = await db.ActionPreviews.AsNoTracking().SingleAsync(p => p.Id == id);
        var step = await db.ActionSteps.AsNoTracking().SingleAsync(s => s.PreviewId == id);

        Assert.Null(await gate.CheckAsync(preview, step, default));
        _watcher.Spent.Add($"{Utxo1}:1");
        Assert.Contains("has been spent", await gate.CheckAsync(preview, step, default));
        Assert.Equal(ActionPreviewStatus.Expired, (await NewContext().ActionPreviews.SingleAsync(p => p.Id == id)).Status);
    }

    [Fact]
    public async Task The_esplora_watcher_reads_spent_state_and_fee_rate()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/outspend/1") => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"spent\":true}") },
            var p when p.EndsWith("/outspend/0") => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"spent\":false}") },
            var p when p.EndsWith("/fees/recommended") => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"fastestFee\":20,\"halfHourFee\":12.5}") },
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
        var watcher = new EsploraBitcoinChainWatcher(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new BitcoinOptions()));
        Assert.True(await watcher.IsSpentAsync("bitcoin", Utxo1, 1, default));
        Assert.False(await watcher.IsSpentAsync("bitcoin", Utxo1, 0, default));
        Assert.True(await watcher.IsSpentAsync("bitcoin", Utxo1, 7, default)); // unknown
        Assert.Equal(12.5m, await watcher.GetFeeRateAsync("bitcoin-testnet", default));
        Assert.Null(await watcher.GetFeeRateAsync("litecoin", default));
    }

    /// <summary>The funding transaction's outputs as a chain would report them, built from the PSBT's inputs; <paramref name="satsDelta"/> falsifies the values.</summary>
    private static IReadOnlyList<BitcoinPrevout>? ChainFrom(string psbt, string txid, long satsDelta, Func<BitcoinInput, bool>? falsify = null)
    {
        var inputs = PsbtDecoder.Decode(psbt, false).Inputs.Where(i => i.Txid == txid).ToList();
        if (inputs.Count == 0) return null;
        var outs = new BitcoinPrevout[(int)inputs.Max(i => i.Vout) + 1];
        for (var n = 0; n < outs.Length; n++) outs[n] = new BitcoinPrevout(1, "6a"); // outputs the PSBT does not spend
        foreach (var i in inputs) outs[(int)i.Vout] = new BitcoinPrevout(i.Sats + (falsify is null || falsify(i) ? satsDelta : 0), i.ScriptPubKey);
        return outs;
    }

    // BCHAT-041: the displayed fee is inputs minus outputs, so every input's value and script must be what the chain has.
    [Fact]
    public async Task Build_refuses_an_input_whose_value_the_chain_does_not_confirm()
    {
        _watcher.Outputs = txid => ChainFrom(_mcp.Psbt(), txid, 1);
        var (svc, db) = NewService();
        await using var _ = db;
        var e = await Assert.ThrowsAsync<BitcoinTransferException>(() => svc.BuildAsync(Ctx, Req, default));
        Assert.Contains("does not match", e.Message);
    }

    [Fact]
    public async Task Build_refuses_when_only_the_second_input_is_wrong()
    {
        var h3 = PsbtFixture.P2wpkh(PsbtFixture.H1);
        _mcp.Psbt = () => PsbtFixture.Build([new PsbtFixture.In(Utxo1, 0, 80000, PsbtFixture.P2wpkh(PsbtFixture.H1)), new PsbtFixture.In(Utxo2, 0, 80000, h3)],
            [(PsbtFixture.P2wpkh(PsbtFixture.H2), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H1), 59000)]);
        var (svc, db) = NewService();
        await using var _ = db;
        var good = await svc.BuildAsync(Ctx, Req, default); // both match: previewed
        Assert.Equal(2, good.Intent.Inputs.Count);

        _watcher.Outputs = txid => ChainFrom(_mcp.Psbt(), txid, 10, i => i.Txid == Utxo2); // the second funding transaction lies
        await Assert.ThrowsAsync<BitcoinTransferException>(() => svc.BuildAsync(Ctx, Req, default));
    }

    [Fact]
    public async Task Build_looks_up_each_funding_transaction_once()
    {
        var spk = PsbtFixture.P2wpkh(PsbtFixture.H1);
        _mcp.Psbt = () => PsbtFixture.Build([new PsbtFixture.In(Utxo1, 0, 50000, spk), new PsbtFixture.In(Utxo1, 1, 50000, spk), new PsbtFixture.In(Utxo2, 0, 50000, spk)],
            [(PsbtFixture.P2wpkh(PsbtFixture.H2), 100000), (PsbtFixture.P2wpkh(PsbtFixture.H1), 49000)]);
        var lookups = new List<string>();
        _watcher.Outputs = txid => { lookups.Add(txid); return ChainFrom(_mcp.Psbt(), txid, 0); };
        var (svc, db) = NewService();
        await using var _ = db;
        await svc.BuildAsync(Ctx, Req, default);
        Assert.Equal([Utxo1, Utxo2], lookups.OrderBy(x => x == Utxo1 ? 0 : 1).ToArray());
    }

    [Fact]
    public async Task Build_refuses_an_input_whose_script_differs_from_the_chain()
    {
        _watcher.Outputs = txid => ChainFrom(_mcp.Psbt(), txid, 0)!.Select(o => new BitcoinPrevout(o.Sats, "0014" + new string('a', 40))).ToList();
        var (svc, db) = NewService();
        await using var _ = db;
        await Assert.ThrowsAsync<BitcoinTransferException>(() => svc.BuildAsync(Ctx, Req, default));
    }

    [Fact]
    public async Task Build_refuses_an_input_the_chain_does_not_know_or_cannot_be_read()
    {
        _watcher.Outputs = _ => null;
        var (svc, db) = NewService();
        await using var _ = db;
        var e = await Assert.ThrowsAsync<BitcoinTransferException>(() => svc.BuildAsync(Ctx, Req, default));
        Assert.Contains("could not be confirmed", e.Message);
    }

    [Fact]
    public async Task Build_without_a_chain_watcher_keeps_the_PSBTs_own_values()
    {
        var (svc, db) = NewService(withWatcher: false);
        await using var _ = db;
        Assert.NotNull((await svc.BuildAsync(Ctx, Req, default)).Intent);
    }

    [Fact]
    public async Task Esplora_watcher_reads_a_transactions_outputs_and_treats_every_failure_as_unknown()
    {
        var txid = Utxo1;
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/tx/" + txid) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"vout\":[{\"value\":1000,\"scriptpubkey\":\"0014aa\"},{\"value\":150000,\"scriptpubkey\":\"0014bb\"}]}") },
            var p when p.EndsWith("/tx/" + Utxo2) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"vout\":[{\"value\":\"lots\",\"scriptpubkey\":\"0014aa\"}]}") },
            var p when p.EndsWith("/tx/" + new string('1', 64)) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"vout\":[{\"value\":5}]}") },
            var p when p.EndsWith("/tx/" + new string('2', 64)) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("not json") },
            var p when p.EndsWith("/tx/" + new string('3', 64)) => throw new HttpRequestException("down"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
        var watcher = new EsploraBitcoinChainWatcher(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new BitcoinOptions()));
        Assert.Equal([new BitcoinPrevout(1000, "0014aa"), new BitcoinPrevout(150000, "0014bb")], await watcher.GetOutputsAsync("bitcoin", txid.ToUpperInvariant(), default));
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", Utxo2, default)); // a non-numeric value
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", new string('1', 64), default)); // an output without a script
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", new string('2', 64), default)); // not JSON
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", new string('3', 64), default)); // the endpoint is down
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", new string('4', 64), default)); // unknown transaction (404)
        Assert.Null(await watcher.GetOutputsAsync("bitcoin", "zz", default)); // not a txid
        Assert.Null(await watcher.GetOutputsAsync("litecoin", txid, default)); // no Esplora for the network
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task Refuses_a_source_address_that_is_not_the_users()
    {
        _mcp.Own = [PsbtFixture.A2];
        var (svc, _) = NewService();
        var outcome = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.True(outcome.IsError);
        Assert.DoesNotContain(_mcp.Calls, c => c.StartsWith("createBitcoinTransaction"));
    }

    [Fact]
    public async Task Accepts_the_users_secondary_address_as_the_source()
    {
        _mcp.Own = [PsbtFixture.A2];
        _mcp.Secondary = PsbtFixture.A1;
        var (svc, _) = NewService();
        var outcome = await svc.StartAsync(Ctx, _conversation, Req, default);
        Assert.False(outcome.IsError, outcome.Message);
        Assert.Contains("getCryptoAddress:bitcoin:1", _mcp.Calls);
        Assert.Contains(_mcp.Calls, c => c.StartsWith("createBitcoinTransaction"));
    }

    [Fact]
    public void Parses_the_tool_arguments()
    {
        var ok = BitcoinTransferCard.ParseRequest(JsonNode.Parse($$"""{"network":"bitcoin","from":"{{PsbtFixture.A1}}","to":"{{PsbtFixture.A2}}","amount":"100000"}""")!, out var p);
        Assert.Equal(new BitcoinTransferRequest("bitcoin", PsbtFixture.A1, PsbtFixture.A2, 100000), ok);
        Assert.Null(p);
        Assert.Null(BitcoinTransferCard.ParseRequest(JsonNode.Parse($$"""{"network":"bitcoin","from":"x","to":"{{PsbtFixture.A2}}","amount":"1"}""")!, out _));
        Assert.Null(BitcoinTransferCard.ParseRequest(JsonNode.Parse($$"""{"network":"bitcoin","from":"{{PsbtFixture.A1}}","to":"{{PsbtFixture.A2}}","amount":"0"}""")!, out _));
    }

    [Fact]
    public void A_transfer_that_needs_several_addresses_is_split_per_address()
    {
        var parts = BitcoinTransferPlanner.Split([("a", 30_000), ("b", 90_000), ("c", 50_000)], 100_000, preferred: "a")!;
        Assert.Equal([("a", 30_000L), ("b", 70_000L)], parts.Select(x => (x.From, x.Sats)));
        Assert.Null(BitcoinTransferPlanner.Split([("a", 30_000)], 100_000));
        Assert.Single(BitcoinTransferPlanner.Split([("a", 300_000), ("b", 1)], 100_000)!);
    }

    [Fact]
    public void Sats_convert_to_the_btc_string_the_builder_takes()
    {
        Assert.Equal("0.001", BitcoinTransferService.SatsToBtc(100000));
        Assert.Equal("0.00000001", BitcoinTransferService.SatsToBtc(1));
        Assert.Equal("1", BitcoinTransferService.SatsToBtc(100_000_000));
        Assert.Equal("21000000", BitcoinTransferService.SatsToBtc(2_100_000_000_000_000));
    }
}
