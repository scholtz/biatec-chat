using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Mcp;

namespace Biatec.Chat.Tools.Wallet.Transfer;

public sealed class TransferException(string message) : Exception(message);

/// <summary>A built, verified single-transaction step.</summary>
public sealed record BuiltTransferStep(GroupedTransactions Group, Transaction Transaction)
{
    public ulong FirstValid => Group.ValidityWindow().FirstValid;
    public ulong LastValid => Group.ValidityWindow().LastValid;
}

/// <summary>
/// Builds one planned step through Biatec MCP (<c>createPaymentTransaction</c>, <c>createOptInTransaction</c>) and
/// checks that what came back is exactly what the preview will say: the backend never hand-assembles a transaction,
/// it only refuses one that does not match the intent. The returned transaction gets its group ID from the SDK.
/// </summary>
public sealed class TransferStepBuilder(IBiatecMcpClient mcp)
{
    public async Task<BuiltTransferStep> BuildAsync(McpCallContext ctx, string network, TransferStepPlan step, CancellationToken ct)
    {
        // Biatec MCP takes its own network code (algorand-testnet); the chat name (testnet) is kept for the checks below.
        var mcpNetwork = McpWalletJson.AlgorandNetwork(network);
        var result = step.Kind switch
        {
            TransferStepKind.OptIn => await mcp.CreateOptInTransactionAsync(ctx, mcpNetwork, step.Sender, step.AssetId, ct),
            TransferStepKind.Fund => await mcp.CreatePaymentTransactionAsync(ctx, mcpNetwork, step.Sender, step.Receiver, step.Amount, null, null, ct),
            _ => await mcp.CreatePaymentTransactionAsync(ctx, mcpNetwork, step.Sender, step.Receiver, step.Amount, step.AssetId == 0 ? null : step.AssetId, null, ct),
        };
        if (result.Failed) throw new TransferException("The transaction could not be built.");
        var txns = Decode(result);
        if (txns.Count != 1) throw new TransferException("The builder did not return exactly one transaction.");
        var tx = txns[0];
        Verify(tx, step, network);
        var group = AlgorandGroup.Ensure([tx]);
        return new BuiltTransferStep(group, group.Transactions[0]);
    }

    /// <summary>Unsigned transactions in an MCP result: every string leaf that decodes as a base64 msgpack transaction.</summary>
    internal static List<Transaction> Decode(McpToolResult result)
    {
        var found = new List<Transaction>();
        if (result.Json is { } json) Walk(json, found);
        else TryAdd(result.Text, found);
        return found;
    }

    private static void Walk(JsonElement e, List<Transaction> found)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: TryAdd(e.GetString(), found); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x, found); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, found); break;
        }
    }

    private static void TryAdd(string? s, List<Transaction> found)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 40) return;
        try
        {
            var tx = Transaction.FromBase64String(s.Trim());
            if (tx?.Sender is not null) found.Add(tx);
        }
        catch (Exception) { /* not a transaction */ }
    }

    private static string? A(Address? a) => a?.EncodeAsString();

    private static bool IsZero(Address? a) => a is null || a.Bytes.All(b => b == 0);

    /// <summary>
    /// Close-out and clawback fields (<c>close</c>, <c>aclose</c>, <c>asnd</c>) are checked on the wire form, which is
    /// what is signed. The note and lease are cleared first so their bytes cannot match a key.
    /// </summary>
    private static bool HasForbiddenField(Transaction tx)
    {
        var copy = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(Algorand.Utils.Encoder.EncodeToMsgPackOrdered(tx));
        copy.Note = null!;
        copy.Lease = null!;
        var wire = Algorand.Utils.Encoder.EncodeToMsgPackOrdered(copy);
        foreach (var key in new[] { "close", "aclose", "asnd" })
        {
            var k = System.Text.Encoding.ASCII.GetBytes(key);
            var needle = new byte[k.Length + 1];
            needle[0] = (byte)(0xa0 | k.Length);
            k.CopyTo(needle, 1);
            if (wire.AsSpan().IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    /// <summary>Highest fee accepted for a single transaction: ten times the network minimum (1000 microALGO).</summary>
    internal const ulong MaxFee = 10_000;

    internal static void Verify(Transaction tx, TransferStepPlan step, string network)
    {
        void Fail(string what) => throw new TransferException($"The built transaction does not match the request ({what}); nothing was previewed.");
        if (A(tx.Sender) != step.Sender) Fail("sender");
        if (!IsZero(tx.RekeyTo)) Fail("rekey");
        var genesis = network == "mainnet" ? "mainnet-v1.0" : "testnet-v1.0";
        if (tx.GenesisId != genesis) Fail("network");
        if (tx.Fee > MaxFee) Fail("fee");
        if (HasForbiddenField(tx)) Fail("close-out or clawback");
        switch (tx)
        {
            case PaymentTransaction pay when step.AssetId == 0 && step.Kind != TransferStepKind.OptIn:
                if (A(pay.Receiver) != step.Receiver) Fail("receiver");
                if ((pay.Amount ?? 0).ToString() != step.Amount) Fail("amount");
                if (!IsZero(pay.CloseRemainderTo)) Fail("close-out");
                break;
            case AssetTransferTransaction axfer when step.AssetId != 0:
                if (axfer.XferAsset != step.AssetId) Fail("asset");
                if (A(axfer.AssetReceiver) != step.Receiver) Fail("receiver");
                if (axfer.AssetAmount.ToString() != step.Amount) Fail("amount");
                break;
            case AssetAcceptTransaction accept when step.Kind == TransferStepKind.OptIn:
                // The SDK reads a zero-amount transfer to oneself as an opt-in.
                if (accept.XferAsset != step.AssetId) Fail("asset");
                break;
            default:
                Fail("transaction type");
                break;
        }
    }
}
