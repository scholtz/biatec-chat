using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Algod;
using Biatec.Chat.Tools.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Tools.Wallet.Transfer;

/// <summary>Result of starting or continuing a transfer. <see cref="Snapshot"/> is null when nothing was previewed.</summary>
public sealed record TransferOutcome(
    PreviewSnapshot? Snapshot,
    TransferRequest? Request,
    string Symbol,
    int Decimals,
    IReadOnlyList<TransferStepPlan> Plan,
    string Message,
    bool IsError = false,
    string? Simulation = null)
{
    public static TransferOutcome Error(string message) => new(null, null, "", 0, [], message, true);
}

/// <summary>
/// <c>wallet.transfer</c> on Algorand: plans the steps, builds only the first through Biatec MCP, wraps it in an
/// <see cref="ActionPreviewService"/> preview, and builds each later step only once the earlier one has landed
/// (<see cref="ActionPreviewService.AddNextStepAsync"/> refuses otherwise). Signing and submission are the confirm
/// flow's; this never signs.
/// </summary>
public sealed class TransferPreviewService(
    IBiatecMcpClient mcp,
    ActionPreviewService previews,
    ChatDbContext db,
    TimeProvider clock,
    AlgodClientFactory? algod = null,
    IAlgodSimulateClient? simulate = null)
{
    private readonly TransferStepBuilder _builder = new(mcp);

    /// <summary>Starts the transfer, or continues / rebuilds the matching earlier preview of this conversation.</summary>
    public async Task<TransferOutcome> StartAsync(McpCallContext ctx, Guid conversationId, TransferRequest request, CancellationToken ct)
    {
        if (request.Network is not ("mainnet" or "testnet"))
            return TransferOutcome.Error("Only Algorand mainnet and testnet are supported here. Bitcoin transfers are not available yet.");

        var existing = await FindExistingAsync(conversationId, request, ct);
        if (existing is not null) return await ContinueAsync(ctx, conversationId, request, existing, ct);

        var own = await mcp.ListAlgorandAddressesAsync(ctx, ct);
        if (own.Failed) return TransferOutcome.Error("The addresses could not be read. Tell the user to try again.");
        var addresses = McpWalletJson.Addresses(own.Json);
        if (!addresses.Contains(request.From, StringComparer.Ordinal))
            return TransferOutcome.Error("The source address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.");

        var fromBal = await mcp.GetCryptoBalanceAsync(ctx, request.StepNetwork, request.From, ct);
        var fromEntries = fromBal.Failed ? null : McpWalletJson.Balances(fromBal.Json);
        if (fromEntries is null) return TransferOutcome.Error("The balance could not be read. Tell the user to try again.");
        var held = fromEntries.FirstOrDefault(b => b.AssetId == request.AssetId);
        if (held is null)
            return TransferOutcome.Error(request.AssetId == 0 ? "The source address holds no ALGO." : $"The source address does not hold asset {request.AssetId}.");
        var symbol = request.AssetId == 0 ? "ALGO" : string.IsNullOrWhiteSpace(held.Symbol) ? $"asset {request.AssetId}" : held.Symbol!;
        if (held.Decimals is null)
            return TransferOutcome.Error($"The number of decimals of {symbol} is unknown, so the amount cannot be shown correctly. Nothing was previewed.");
        var decimals = held.Decimals.Value;
        if (BigInteger.Parse(request.Amount, CultureInfo.InvariantCulture) > BigInteger.Parse(held.Amount, CultureInfo.InvariantCulture))
            return TransferOutcome.Error($"The source address holds only {TransferPlanner.FormatAmount(held.Amount, decimals)} {symbol}, less than the requested {TransferPlanner.FormatAmount(request.Amount, decimals)}.");

        var receiverOwn = addresses.Contains(request.To, StringComparer.Ordinal);
        var optedIn = true;
        BigInteger receiverAlgo = 0;
        if (request.AssetId != 0 && receiverOwn && request.To != request.From)
        {
            var toBal = await mcp.GetCryptoBalanceAsync(ctx, request.StepNetwork, request.To, ct);
            var entries = toBal.Failed ? null : McpWalletJson.Balances(toBal.Json);
            if (entries is null) return TransferOutcome.Error("The receiving address could not be read. Tell the user to try again.");
            optedIn = entries.Any(b => b.AssetId == request.AssetId);
            receiverAlgo = entries.Where(b => b.AssetId == 0).Select(b => BigInteger.Parse(b.Amount, CultureInfo.InvariantCulture)).FirstOrDefault();
        }

        var plan = TransferPlanner.Plan(request, receiverOwn, optedIn, receiverAlgo, symbol);
        var canonical = JsonCanonicalizer.Canonicalize(TransferPlanner.ToJson(request, symbol, decimals, plan));
        return await BuildAndCreateAsync(ctx, conversationId, request, symbol, decimals, plan, canonical, plan[0], rebuildOf: null, ct);
    }

    private async Task<ActionPreview?> FindExistingAsync(Guid conversationId, TransferRequest request, CancellationToken ct)
    {
        var candidates = await db.ActionPreviews
            .Where(p => p.ConversationId == conversationId && p.Capability == "wallet.transfer" && p.RebuiltAsPreviewId == null
                        && (p.Status == ActionPreviewStatus.Proposed || p.Status == ActionPreviewStatus.InProgress
                            || p.Status == ActionPreviewStatus.Expired || p.Status == ActionPreviewStatus.Confirmed))
            .ToListAsync(ct);
        return candidates.Where(p => TransferPlanner.FromJson(p.CanonicalJson) is { } j && j.Request.SameAs(request))
            .OrderByDescending(p => p.ExpiresAt).FirstOrDefault();
    }

    private async Task<TransferOutcome> ContinueAsync(McpCallContext ctx, Guid conversationId, TransferRequest request, ActionPreview preview, CancellationToken ct)
    {
        var (_, symbol, decimals, plan) = TransferPlanner.FromJson(preview.CanonicalJson)!.Value;
        var snap = await previews.GetAsync(conversationId, preview.Id, null, ct);
        var steps = snap.Steps;

        if (steps.Any(s => s.Status is StepStatus.Submitted or StepStatus.AwaitingConsent or StepStatus.Signed) || preview.Status == ActionPreviewStatus.Confirmed)
            return new TransferOutcome(snap, request, symbol, decimals, plan, "A step of this transfer is being signed or is waiting to land. Wait for it before asking again.", Simulation: null);

        if (preview.Status == ActionPreviewStatus.InProgress && steps.All(s => s.Status == StepStatus.Confirmed))
        {
            if (steps.Count >= plan.Count)
                return new TransferOutcome(snap, request, symbol, decimals, plan, "This transfer is complete.");
            // The earlier step landed: build the next one now, and only now.
            var next = plan[steps.Count];
            BuiltSpec built;
            PreviewSnapshot added;
            try
            {
                built = await BuildStepAsync(ctx, request, next, ct);
                if (built.SimulationFailed)
                    return TransferOutcome.Error("The next transaction would fail on chain, so it was not previewed: " + built.Simulation);
                added = await previews.AddNextStepAsync(conversationId, preview.Id, built.Spec, ct);
            }
            catch (Exception e) when (e is TransferException or PreviewException) { return TransferOutcome.Error(e.Message); }
            return new TransferOutcome(added, request, symbol, decimals, plan,
                $"Step {steps.Count + 1} of {plan.Count} is ready for the user's confirmation.", Simulation: built.Simulation);
        }

        // Still awaiting confirmation: keep the same preview while it is usable, otherwise rebuild the pending step.
        var pending = steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
        if (pending is not null && preview.Status == ActionPreviewStatus.Proposed && snap.Countdown.CanConfirm)
            return new TransferOutcome(snap, request, symbol, decimals, plan, "The preview is waiting for the user's confirmation.");

        var firstUnlanded = steps.Count(s => s.Status == StepStatus.Confirmed);
        if (firstUnlanded >= plan.Count) return new TransferOutcome(snap, request, symbol, decimals, plan, "This transfer is complete.");
        return await BuildAndCreateAsync(ctx, conversationId, request, symbol, decimals, plan, preview.CanonicalJson, plan[firstUnlanded], rebuildOf: preview.Id, ct);
    }

    private async Task<TransferOutcome> BuildAndCreateAsync(McpCallContext ctx, Guid conversationId, TransferRequest request, string symbol, int decimals,
        IReadOnlyList<TransferStepPlan> plan, string canonical, TransferStepPlan first, Guid? rebuildOf, CancellationToken ct)
    {
        BuiltSpec built;
        try { built = await BuildStepAsync(ctx, request, first, ct); }
        catch (TransferException e) { return TransferOutcome.Error(e.Message); }
        if (built.SimulationFailed)
            return TransferOutcome.Error("The transaction would fail on chain, so no preview was made: " + built.Simulation);

        PreviewSnapshot snap;
        try
        {
            snap = rebuildOf is { } old
                ? await previews.RebuildAsync(conversationId, old, canonical, [built.Spec], ct: ct)
                : await previews.CreateAsync(new NewPreviewSpec(conversationId, "wallet.transfer", canonical, [built.Spec]), ct);
        }
        catch (PreviewException e) { return TransferOutcome.Error(e.Message); }

        var msg = plan.Count == 1
            ? "The preview is ready for the user's confirmation."
            : $"Step 1 of {plan.Count} is ready for the user's confirmation. The later steps are built one at a time, each after the previous one lands; " +
              "when the user says it has landed, call wallet.transfer again with the same arguments to build the next step.";
        return new TransferOutcome(snap, request, symbol, decimals, plan, msg, Simulation: built.Simulation);
    }

    private IAlgodSimulateClient? SimulatorFor(string stepNetwork)
    {
        if (simulate is not null) return simulate;
        if (algod is null) return null;
        try { return algod.For(stepNetwork); }
        catch (ArgumentException) { return null; }
    }

    private sealed record BuiltSpec(NewStepSpec Spec, string? Simulation, bool SimulationFailed);

    private async Task<BuiltSpec> BuildStepAsync(McpCallContext ctx, TransferRequest request, TransferStepPlan step, CancellationToken ct)
    {
        var built = await _builder.BuildAsync(ctx, request.Network, step, ct);
        string? simText = null;
        var failed = false;
        ulong currentRound = built.FirstValid;
        var sim = SimulatorFor(request.StepNetwork);
        if (sim is not null)
        {
            try
            {
                var outcome = await sim.SimulateAsync(built.Group.Transactions, ct: ct);
                simText = outcome.Summary;
                failed = !outcome.Success;
                currentRound = await sim.GetLastRoundAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                simText = "Simulation was not available.";
            }
        }
        var now = clock.GetUtcNow();
        var expires = PreviewPolicy.ExpiryFromLastValid(built.LastValid, currentRound, now);
        var spec = new NewStepSpec(ActionStepKind.Sign, built.Group.GroupHash, expires,
            UnsignedGroup: System.Text.Json.JsonSerializer.Serialize(built.Group.ToBase64()),
            SenderAddress: step.Sender, Network: request.StepNetwork,
            FirstValid: (long)built.FirstValid, LastValid: (long)built.LastValid);
        return new BuiltSpec(spec, simText, failed);
    }
}
