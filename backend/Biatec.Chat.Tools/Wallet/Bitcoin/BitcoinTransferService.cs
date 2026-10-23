using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Wallet.Bitcoin;

/// <summary>What the user asked for. <see cref="Sats"/> is whole satoshis; <see cref="Network"/> is <c>bitcoin</c> (Biatec MCP serves no Bitcoin testnet).</summary>
public sealed record BitcoinTransferRequest(string Network, string From, string To, long Sats)
{
    public bool Testnet => BitcoinAddresses.IsTestnet(Network);
    public bool SameAs(BitcoinTransferRequest o) => Network == o.Network && From == o.From && To == o.To && Sats == o.Sats;
}

/// <summary>Chain facts the backend watches while a Bitcoin preview is pending. Implemented against a node or explorer; mocked in tests.</summary>
public interface IBitcoinChainWatcher
{
    /// <summary>True when the output <paramref name="txid"/>:<paramref name="vout"/> has been spent (or is unknown to the chain).</summary>
    Task<bool> IsSpentAsync(string network, string txid, uint vout, CancellationToken ct);

    /// <summary>Current recommended fee rate in sat/vB, or null when it cannot be read.</summary>
    Task<decimal?> GetFeeRateAsync(string network, CancellationToken ct);

    /// <summary>The output <paramref name="txid"/>:<paramref name="vout"/> as the chain has it, or null when it is unknown or cannot be read (BCHAT-041).</summary>
    Task<BitcoinPrevout?> GetPrevoutAsync(string network, string txid, uint vout, CancellationToken ct);
}

/// <summary>An output as the chain reports it: its value and its script (lowercase hex).</summary>
public sealed record BitcoinPrevout(long Sats, string ScriptPubKey);

public sealed record BitcoinOutcome(PreviewSnapshot? Snapshot, BitcoinTransferRequest? Request, BitcoinIntent? Intent, string Message, bool IsError = false)
{
    public static BitcoinOutcome Error(string message) => new(null, null, null, message, true);
}

public enum BitcoinInvalidation { None, TimeExpired, InputSpent, FeeRateMoved }

public sealed class BitcoinTransferException(string message) : Exception(message);

/// <summary>
/// <c>wallet.transfer</c> on Bitcoin. The transaction is built by Biatec MCP <c>createBitcoinTransaction</c> and returned as a
/// PSBT; this decodes it, checks that it is the one the user asked for from one sender address, and binds the preview to
/// the intent hash. A preview lives 30 minutes and is invalidated when an input is spent or the fee rate moves by more
/// than 25 percent; the next call rebuilds it. Signing and broadcasting belong to the confirm flow.
/// </summary>
public sealed class BitcoinTransferService(
    IBiatecMcpClient mcp,
    ActionPreviewService previews,
    ChatDbContext db,
    TimeProvider clock,
    IBitcoinChainWatcher? watcher = null)
{
    /// <summary>A preview is invalidated when the fee rate moves by more than this fraction of the rate it was built at.</summary>
    public const decimal FeeRateTolerance = 0.25m;

    /// <summary>Refuse a built transaction whose estimated fee rate is above this (sat/vB): a builder fault, not a user wish.</summary>
    public const decimal MaxFeeRate = 1000m;

    private static readonly Regex AddressShape = new(@"\A[A-Za-z0-9]{26,90}\z", RegexOptions.Compiled);

    public async Task<BitcoinOutcome> StartAsync(McpCallContext ctx, Guid conversationId, BitcoinTransferRequest request, CancellationToken ct)
    {
        var existing = await FindExistingAsync(conversationId, request, ct);
        if (existing is not null)
        {
            var snap = await previews.GetAsync(conversationId, existing.Id, null, ct);
            var step = snap.Steps.OrderBy(s => s.Seq).Last();
            if (InFlight(snap.Steps))
                return new BitcoinOutcome(snap, request, Decode(step, request), "This transfer is being signed or is waiting to land. Wait for it before asking again.");

            var reason = existing.Status == ActionPreviewStatus.Proposed ? await CheckAsync(conversationId, existing.Id, ct) : BitcoinInvalidation.TimeExpired;
            if (reason == BitcoinInvalidation.None)
            {
                snap = await previews.GetAsync(conversationId, existing.Id, null, ct);
                return new BitcoinOutcome(snap, request, Decode(step, request), "The preview is waiting for the user's confirmation.");
            }
            return await BuildAndCreateAsync(ctx, conversationId, request, rebuildOf: existing.Id, ct);
        }

        var own = await BitcoinOwnAddresses.ReadAsync(mcp, ctx, request.Network, ct);
        if (own is null) return BitcoinOutcome.Error("The addresses could not be read. Tell the user to try again.");
        if (BitcoinOwnAddresses.Find(own, request.From) is null)
            return BitcoinOutcome.Error("The source address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.");
        return await BuildAndCreateAsync(ctx, conversationId, request, rebuildOf: null, ct);
    }

    /// <summary>
    /// Re-checks a pending Bitcoin preview: lapsed 30 minutes, an input spent, or a fee rate that moved by more than
    /// 25 percent. A preview that fails is marked expired so it cannot be confirmed; the next transfer call rebuilds it.
    /// </summary>
    public async Task<BitcoinInvalidation> CheckAsync(Guid conversationId, Guid previewId, CancellationToken ct)
    {
        var preview = await db.ActionPreviews.AsNoTracking().SingleOrDefaultAsync(p => p.Id == previewId && p.ConversationId == conversationId, ct)
            ?? throw new PreviewException(PreviewError.NotFound, "Preview not found.");
        if (preview.Status != ActionPreviewStatus.Proposed) return BitcoinInvalidation.None;
        var step = await db.ActionSteps.AsNoTracking().Where(s => s.PreviewId == previewId && s.Status == StepStatus.Pending)
            .OrderByDescending(s => s.Seq).FirstOrDefaultAsync(ct);
        if (step is null) return BitcoinInvalidation.None;

        var reason = BitcoinInvalidation.None;
        if (step.ExpiresAt <= clock.GetUtcNow()) reason = BitcoinInvalidation.TimeExpired;
        else if (watcher is not null && step.Network is { } net && StoredIntent(step, net) is { } intent)
        {
            foreach (var i in intent.Inputs)
                if (await watcher.IsSpentAsync(net, i.Txid, i.Vout, ct)) { reason = BitcoinInvalidation.InputSpent; break; }
            if (reason == BitcoinInvalidation.None && await watcher.GetFeeRateAsync(net, ct) is { } now)
            {
                var built = intent.FeeRate();
                if (built > 0 && Math.Abs(now - built) / built > FeeRateTolerance) reason = BitcoinInvalidation.FeeRateMoved;
            }
        }
        if (reason != BitcoinInvalidation.None) await ExpireAsync(previewId, ct);
        return reason;
    }

    private async Task ExpireAsync(Guid previewId, CancellationToken ct)
    {
        var n = await db.ActionPreviews
            .Where(p => p.Id == previewId && p.Status == ActionPreviewStatus.Proposed
                        && !db.ActionSteps.Any(x => x.PreviewId == p.Id && (x.Status == StepStatus.Signed || x.Status == StepStatus.Submitted || x.Status == StepStatus.AwaitingConsent)))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ActionPreviewStatus.Expired), ct);
        if (n == 0) return;
        await db.ActionSteps.Where(x => x.PreviewId == previewId && x.Status == StepStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, StepStatus.Expired), ct);
        foreach (var e in db.ChangeTracker.Entries<ActionPreview>().Where(e => e.Entity.Id == previewId).ToList()) e.State = EntityState.Detached;
        foreach (var e in db.ChangeTracker.Entries<ActionStep>().Where(e => e.Entity.PreviewId == previewId).ToList()) e.State = EntityState.Detached;
    }

    private static bool InFlight(IEnumerable<ActionStep> steps) =>
        steps.Any(s => s.Status is StepStatus.Submitted or StepStatus.AwaitingConsent or StepStatus.Signed);

    /// <summary>
    /// The preview a repeated request reuses: a Proposed or Expired one, or one that is still in flight (so it is not
    /// started twice). Completed, failed and cancelled previews never match, so an identical transfer can be repeated.
    /// </summary>
    private async Task<ActionPreview?> FindExistingAsync(Guid conversationId, BitcoinTransferRequest request, CancellationToken ct)
    {
        var candidates = await db.ActionPreviews
            .Where(p => p.ConversationId == conversationId && p.Capability == "wallet.transfer" && p.RebuiltAsPreviewId == null
                        && (p.Status == ActionPreviewStatus.Proposed || p.Status == ActionPreviewStatus.Expired
                            || ((p.Status == ActionPreviewStatus.Confirmed || p.Status == ActionPreviewStatus.InProgress)
                                && db.ActionSteps.Any(x => x.PreviewId == p.Id && (x.Status == StepStatus.Submitted || x.Status == StepStatus.AwaitingConsent || x.Status == StepStatus.Signed)))))
            .ToListAsync(ct);
        return candidates.Where(p => FromJson(p.CanonicalJson) is { } r && r.SameAs(request)).OrderByDescending(p => p.ExpiresAt).FirstOrDefault();
    }

    private async Task<BitcoinOutcome> BuildAndCreateAsync(McpCallContext ctx, Guid conversationId, BitcoinTransferRequest request, Guid? rebuildOf, CancellationToken ct)
    {
        BitcoinIntent intent;
        string psbt;
        try { (intent, psbt) = await BuildAsync(ctx, request, ct); }
        catch (BitcoinTransferException e) { return BitcoinOutcome.Error(e.Message); }

        var expires = clock.GetUtcNow().Add(PreviewPolicy.BitcoinLifetime);
        var spec = new NewStepSpec(ActionStepKind.Sign, intent.Hash(), expires,
            UnsignedGroup: JsonSerializer.Serialize(new[] { psbt }), SenderAddress: request.From, Network: request.Network);
        var canonical = JsonCanonicalizer.Canonicalize(ToJson(request));
        try
        {
            var snap = rebuildOf is { } old
                ? await previews.RebuildAsync(conversationId, old, canonical, [spec], ct: ct)
                : await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", canonical, [spec]), ct);
            return new BitcoinOutcome(snap, request, intent, "The preview is ready for the user's confirmation.");
        }
        catch (PreviewException e) { return BitcoinOutcome.Error(e.Message); }
    }

    /// <summary>Builds through Biatec MCP and refuses anything that is not exactly the requested single-sender transfer.</summary>
    public async Task<(BitcoinIntent Intent, string Psbt)> BuildAsync(McpCallContext ctx, BitcoinTransferRequest request, CancellationToken ct)
    {
        var result = await mcp.CreateBitcoinTransactionAsync(ctx, request.Network, request.From, request.To, SatsToBtc(request.Sats), ct);
        if (result.IsError) throw new BitcoinTransferException("The transaction could not be built.");
        var psbts = new List<string>();
        if (result.Json is { } json) Walk(json, psbts); else if (PsbtDecoder.LooksLikePsbt(result.Text ?? "")) psbts.Add(result.Text!.Trim());
        if (psbts.Count != 1) throw new BitcoinTransferException("The builder did not return exactly one PSBT.");

        BitcoinIntent intent;
        try { intent = PsbtDecoder.Decode(psbts[0], request.Testnet); }
        catch (PsbtException e) { throw new BitcoinTransferException("The builder returned a PSBT that cannot be shown, so nothing was previewed: " + e.Message); }
        Verify(intent, request);
        await VerifyPrevoutsAsync(intent, request.Network, ct);
        return (intent, psbts[0]);
    }

    /// <summary>
    /// The fee on the card is inputs minus outputs, and an input that carries only a witness_utxo has a value the builder asserts. Every input's
    /// value and script must equal what the chain reports for the output it spends; an output the chain does not know, or cannot be read,
    /// is refused (BCHAT-041). Without a chain watcher the PSBT's own data stands, as before.
    /// </summary>
    private async Task VerifyPrevoutsAsync(BitcoinIntent intent, string network, CancellationToken ct)
    {
        if (watcher is null) return;
        foreach (var input in intent.Inputs)
        {
            var onChain = await watcher.GetPrevoutAsync(network, input.Txid, input.Vout, ct);
            if (onChain is null)
                throw new BitcoinTransferException("The value of an input could not be confirmed on the Bitcoin chain, so nothing was previewed.");
            if (onChain.Sats != input.Sats || !string.Equals(onChain.ScriptPubKey, input.ScriptPubKey, StringComparison.OrdinalIgnoreCase))
                throw new BitcoinTransferException("An input does not match the output it spends on the Bitcoin chain, so nothing was previewed.");
        }
    }

    /// <summary>Checks the decoded transaction against the request. The user must see exactly what they asked for.</summary>
    public static void Verify(BitcoinIntent intent, BitcoinTransferRequest r)
    {
        if (intent.Inputs.Count == 0) throw new BitcoinTransferException("The transaction has no inputs.");
        if (intent.Inputs.Any(i => i.Address != r.From))
            throw new BitcoinTransferException("The transaction spends from an address other than the source address. A Bitcoin transaction has one sender address, so nothing was previewed.");
        var toReceiver = intent.Outputs.Where(o => o.Address == r.To).ToList();
        var others = intent.Outputs.Where(o => o.Address != r.To).ToList();
        if (r.To == r.From)
        {
            // A transfer to itself: one output of the requested amount is the payment, the rest is change, and every
            // output must go back to the source address.
            var pay = intent.Outputs.FirstOrDefault(o => o.Address == r.To && o.Sats == r.Sats);
            if (pay is null || intent.Outputs.Count > 2) throw new BitcoinTransferException("The transaction does not pay the requested amount.");
            if (intent.Outputs.Any(o => o.Address != r.From))
                throw new BitcoinTransferException("The transaction has outputs other than the payment and change back to the source address.");
        }
        else
        {
            if (toReceiver.Count != 1 || toReceiver[0].Sats != r.Sats)
                throw new BitcoinTransferException("The transaction does not pay the requested amount to the requested address.");
            if (others.Count > 1 || others.Any(o => o.Address != r.From))
                throw new BitcoinTransferException("The transaction has outputs other than the payment and change back to the source address.");
        }
        if (intent.FeeSats <= 0) throw new BitcoinTransferException("The transaction pays no fee.");
        if (intent.FeeRate() > MaxFeeRate) throw new BitcoinTransferException("The transaction's fee rate is unreasonably high, so nothing was previewed.");
    }

    /// <summary>The change output of a transfer: the output back to the sender that is not the payment.</summary>
    public static BitcoinOutput? Change(BitcoinIntent intent, BitcoinTransferRequest r)
    {
        var outs = intent.Outputs.Where(o => o.Address == r.From).ToList();
        if (r.To != r.From) return outs.FirstOrDefault();
        var skipped = false;
        foreach (var o in outs) { if (!skipped && o.Sats == r.Sats) { skipped = true; continue; } return o; }
        return null;
    }

    public static string SatsToBtc(long sats) =>
        (sats / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture);

    public static BitcoinIntent? Decode(ActionStep step, BitcoinTransferRequest request) => StoredIntent(step, request.Network);

    private static BitcoinIntent? StoredIntent(ActionStep step, string network)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(step.UnsignedGroup ?? "[]");
            return list is { Count: > 0 } ? PsbtDecoder.Decode(list[0], BitcoinAddresses.IsTestnet(network)) : null;
        }
        catch (Exception e) when (e is JsonException or PsbtException) { return null; }
    }

    public static JsonObject ToJson(BitcoinTransferRequest r) => new()
    {
        ["capability"] = "wallet.transfer",
        ["network"] = r.Network,
        ["from"] = r.From,
        ["to"] = r.To,
        ["amount"] = r.Sats.ToString(CultureInfo.InvariantCulture),
        ["symbol"] = "BTC",
        ["decimals"] = 8,
    };

    public static BitcoinTransferRequest? FromJson(string canonical)
    {
        try
        {
            var o = JsonNode.Parse(canonical);
            var network = (string?)o?["network"];
            if (network is null || !network.StartsWith("bitcoin", StringComparison.Ordinal)) return null;
            return new BitcoinTransferRequest(network, (string)o!["from"]!, (string)o["to"]!, long.Parse((string)o["amount"]!, CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is JsonException or NullReferenceException or FormatException or InvalidCastException) { return null; }
    }

    /// <summary>The user's own Bitcoin addresses in a <c>getCryptoAddress</c> result: every string leaf shaped like an address.</summary>
    public static IReadOnlyList<string> OwnAddresses(McpToolResult result)
    {
        var found = new List<string>();
        if (result.Json is { } json) WalkAddresses(json, found);
        else if (result.Text is { } t) foreach (var part in t.Split([' ', ',', '\n', '"', '[', ']', '{', '}', ':'], StringSplitOptions.RemoveEmptyEntries)) if (AddressShape.IsMatch(part)) found.Add(part);
        return found;
    }

    private static void WalkAddresses(JsonElement e, List<string> found)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: if (e.GetString() is { } s && AddressShape.IsMatch(s)) found.Add(s); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) WalkAddresses(x, found); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) WalkAddresses(p.Value, found); break;
        }
    }

    private static void Walk(JsonElement e, List<string> found)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: if (e.GetString() is { } s && PsbtDecoder.LooksLikePsbt(s)) found.Add(s.Trim()); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x, found); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, found); break;
        }
    }
}

/// <summary>
/// Splits a transfer that needs funds from several of the user's addresses into one transaction per address, because a
/// Bitcoin transaction has one sender address and one signing call (PRODUCT-DESCRIPTION.md, "Consent"). Fees are not
/// modelled here: each part is then built and checked on its own.
/// </summary>
public static class BitcoinTransferPlanner
{
    public sealed record Part(string From, long Sats);

    /// <summary>Takes from <paramref name="preferred"/> first, then from the largest balances. Null when the balances do not cover the amount.</summary>
    public static IReadOnlyList<Part>? Split(IReadOnlyList<(string Address, long Sats)> balances, long amount, string? preferred = null)
    {
        if (amount <= 0) return null;
        var ordered = balances.Where(b => b.Sats > 0)
            .OrderByDescending(b => b.Address == preferred).ThenByDescending(b => b.Sats).ToList();
        var parts = new List<Part>();
        var left = amount;
        foreach (var (address, sats) in ordered)
        {
            if (left == 0) break;
            var take = Math.Min(left, sats);
            parts.Add(new Part(address, take));
            left -= take;
        }
        return left == 0 ? parts : null;
    }
}
