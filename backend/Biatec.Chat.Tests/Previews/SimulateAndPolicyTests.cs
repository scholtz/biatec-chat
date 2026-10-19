using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;

namespace Biatec.Chat.Tests.Previews;

public sealed class SimulateAndPolicyTests
{
    private sealed class FakeTransport(SimulateResponse response, ulong round = 1000) : IAlgodTransport
    {
        public SimulateRequest? Last { get; private set; }
        public Task<SimulateResponse> SimulateAsync(SimulateRequest request, CancellationToken ct) { Last = request; return Task.FromResult(response); }
        public Task<ulong> GetLastRoundAsync(CancellationToken ct) => Task.FromResult(round);
    }

    private static Transaction Pay() => new PaymentTransaction
    {
        Sender = new Address("UPYAFLHSIPMJOHVXU2MPLQ46GXJKSDCEMZ6RLCQ7GWB5PRDKJUWKKXECXI"),
        Receiver = new Address("KV2XGKMXGYJ6PWYQA5374BYIQBL3ONRMSIARPCFCJEAMAHQEVYPB7PL3KU"),
        Amount = 1, Fee = 1000, FirstValid = 1, LastValid = 1001, GenesisId = "devnet-v1.0",
        GenesisHash = new Digest(Convert.FromBase64String("sC3P7e2SdbqKJK0tbiCdK9tdSpbe6XeCGKdoNzmlj0E=")),
    };

    [Fact]
    public async Task Simulate_SendsUnsignedGroup_WithEmptySignaturesAllowed()
    {
        var transport = new FakeTransport(new SimulateResponse
        {
            LastRound = 4242,
            TxnGroups = [new SimulateTransactionGroupResult { AppBudgetConsumed = 7 }],
        });
        var outcome = await new AlgodSimulateClient(transport).SimulateAsync(AlgorandGroup.Ensure([Pay()]).Transactions);

        Assert.True(outcome.Success);
        Assert.Equal(4242UL, outcome.LastRound);
        Assert.Equal(7UL, outcome.AppBudgetConsumed);
        Assert.True(transport.Last!.AllowEmptySignatures);
        var group = Assert.Single(transport.Last.TxnGroups);
        Assert.Single(group.Txns);
        Assert.Contains("succeeded", outcome.Summary);
    }

    [Fact]
    public async Task Simulate_ReportsFailure()
    {
        var transport = new FakeTransport(new SimulateResponse
        {
            LastRound = 5,
            TxnGroups = [new SimulateTransactionGroupResult { FailureMessage = "overspend", FailedAt = [0UL] }],
        });
        var outcome = await new AlgodSimulateClient(transport).SimulateAsync([Pay()]);

        Assert.False(outcome.Success);
        Assert.Equal("overspend", outcome.FailureMessage);
        Assert.Equal([0UL], outcome.FailedAt);
        Assert.Contains("overspend", outcome.Summary);
    }

    private sealed class ThrowingTransport : IAlgodTransport
    {
        public Task<SimulateResponse> SimulateAsync(SimulateRequest request, CancellationToken ct) => throw new HttpRequestException("400 bad request");
        public Task<ulong> GetLastRoundAsync(CancellationToken ct) => Task.FromResult(1UL);
    }

    [Fact]
    public async Task Simulate_TransportError_IsAFailedOutcome_NotACrash()
    {
        var outcome = await new AlgodSimulateClient(new ThrowingTransport()).SimulateAsync([Pay()]);
        Assert.False(outcome.Success);
        Assert.Contains("400", outcome.FailureMessage);
    }

    [Fact]
    public async Task Simulate_NoGroupResult_IsAFailure()
    {
        var outcome = await new AlgodSimulateClient(new FakeTransport(new SimulateResponse { TxnGroups = [] })).SimulateAsync([Pay()]);
        Assert.False(outcome.Success);
    }

    [Fact]
    public void Factory_ReturnsOneClientPerNetwork_AndRejectsUnknown()
    {
        var a = new AlgodSimulateClient(new FakeTransport(new SimulateResponse()));
        var b = new AlgodSimulateClient(new FakeTransport(new SimulateResponse()));
        var factory = new AlgodClientFactory(new Dictionary<string, IAlgodSimulateClient> { ["algorand-mainnet"] = a, ["algorand-testnet"] = b });

        Assert.Same(a, factory.For("algorand-mainnet"));
        Assert.Same(b, factory.For("ALGORAND-TESTNET"));
        Assert.Throws<ArgumentException>(() => factory.For("nope"));
    }

    [Fact]
    public void Factory_FromOptions_BuildsAClientPerConfiguredNetwork()
    {
        var factory = new AlgodClientFactory(new Dictionary<string, AlgodNetworkOptions>
        {
            ["algorand-mainnet"] = new("https://mainnet-api.4160.nodely.dev"),
            ["algorand-testnet"] = new("https://testnet-api.4160.nodely.dev", "token"),
        });
        Assert.NotSame(factory.For("algorand-mainnet"), factory.For("algorand-testnet"));
        Assert.Same(factory.For("algorand-mainnet"), factory.For("algorand-mainnet"));
    }

    [Theory]
    [InlineData(1001UL, 1000UL, 2)]      // 1 round -> floor(2.7) s
    [InlineData(2000UL, 1000UL, 2700)]   // 1000 rounds
    [InlineData(9000UL, 1000UL, 2700)]   // capped at the 1000 round maximum
    [InlineData(1000UL, 1000UL, 0)]      // already at last valid
    [InlineData(900UL, 1000UL, 0)]       // already past
    public void ExpiryFromLastValid_UsesRoundsRemaining_CappedAt1000(ulong lastValid, ulong current, int seconds)
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddSeconds(seconds), PreviewPolicy.ExpiryFromLastValid(lastValid, current, now));
    }

    [Theory]
    [InlineData(3000, true)]
    [InlineData(600, true)]
    [InlineData(599, false)]
    [InlineData(0, false)]
    public void CanConfirm_NeedsAtLeast600Seconds(int remaining, bool expected) =>
        Assert.Equal(expected, PreviewPolicy.CanConfirm(remaining));

    [Fact]
    public void RemainingSeconds_IsTheSmallerOfGroupAndSession_AndNeverNegative()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(1000, PreviewPolicy.RemainingSeconds(now.AddSeconds(1000), now));
        Assert.Equal(500, PreviewPolicy.RemainingSeconds(now.AddSeconds(1000), now, now.AddSeconds(500)));
        Assert.Equal(1000, PreviewPolicy.RemainingSeconds(now.AddSeconds(1000), now, now.AddSeconds(5000)));
        Assert.Equal(0, PreviewPolicy.RemainingSeconds(now.AddSeconds(-5), now));
    }
}
