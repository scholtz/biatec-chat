using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Biatec.Chat.Tools.Wallet.Bitcoin;
using Biatec.Chat.Tools.Wallet.Transfer;
using CoreStatus = Biatec.Chat.Core.Data.ActionPreviewStatus;
using CoreStep = Biatec.Chat.Core.Data.ActionStep;
using StepStatus = Biatec.Chat.Core.Data.StepStatus;

namespace Biatec.Chat.Api.Wallet.Transfer;

/// <summary>
/// <c>wallet.transfer</c> for Algorand: ALGO or any ASA, with the opt-in steps when the receiver is the user's own
/// un-opted address. The ToolGate has checked tier, scope and schema; tier is re-checked here. The transactions
/// come from Biatec MCP; the result is a numbered preview card whose first step alone is built and bound to a hash.
/// </summary>
public sealed class WalletTransferExecutor(IServiceScopeFactory scopes) : IToolExecutor
{
    public string CapabilityName => "wallet.transfer";
    public string? StatusLabel(JsonNode arguments) => "Preparing your transfer";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        if (context.Tier != Biatec.Chat.Core.Data.SessionTier.Authenticated)
            return new ToolExecutionResult("This needs a signed-in user. Call requestSignIn instead.", true);

        if ((string?)arguments["network"] is "bitcoin" or "bitcoin-testnet")
        {
            // The gate already refuses it when the schema does not offer bitcoin; checked again here so no path that skips the gate
            // can build a preview Confirm could never sign (BCHAT-037).
            var offered = context.Capability.InputSchema?["properties"]?["network"]?["enum"]?.AsArray().Any(n => (string?)n == "bitcoin") == true;
            if (!offered) return new ToolExecutionResult("Bitcoin transfers are not available yet. Tell the user; Bitcoin addresses and balances can still be read.", true);
            return await ExecuteBitcoinAsync(context, arguments, ct);
        }

        var request = ParseRequest(arguments, out var problem);
        if (request is null) return new ToolExecutionResult(problem!, true);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TransferPreviewService>();
            var outcome = await service.StartAsync(new McpCallContext(context.TenantId, context.SessionId, context.Scopes), context.ConversationId, request, ct);
            if (outcome.IsError || outcome.Snapshot is null) return new ToolExecutionResult(outcome.Message, true);

            var card = new PreviewCardEvent(TransferCard.Map(outcome));
            var content = new JsonObject
            {
                ["previewId"] = outcome.Snapshot.Preview.Id.ToString(),
                ["status"] = "awaiting_user_confirmation",
                ["totalSteps"] = outcome.Plan.Count,
                ["note"] = outcome.Message + " The preview card is shown to the user; do not repeat or invent its details.",
            };
            return new ToolExecutionResult(content.ToJsonString(), Events: [card]);
        }
        catch (McpAuthenticationException)
        {
            return new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true);
        }
        catch (McpToolUnavailableException)
        {
            return new ToolExecutionResult("The wallet service is temporarily unavailable. Tell the user to try again later.", true);
        }
    }

    private async Task<ToolExecutionResult> ExecuteBitcoinAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        var request = BitcoinTransferCard.ParseRequest(arguments, out var problem);
        if (request is null) return new ToolExecutionResult(problem!, true);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BitcoinTransferService>();
            var outcome = await service.StartAsync(new McpCallContext(context.TenantId, context.SessionId, context.Scopes), context.ConversationId, request, ct);
            if (outcome.IsError || outcome.Snapshot is null || outcome.Intent is null) return new ToolExecutionResult(outcome.Message, true);

            var card = new PreviewCardEvent(BitcoinTransferCard.Map(outcome));
            var content = new JsonObject
            {
                ["previewId"] = outcome.Snapshot.Preview.Id.ToString(),
                ["status"] = "awaiting_user_confirmation",
                ["totalSteps"] = 1,
                ["note"] = outcome.Message + " The preview card is shown to the user; do not repeat or invent its details.",
            };
            return new ToolExecutionResult(content.ToJsonString(), Events: [card]);
        }
        catch (McpAuthenticationException)
        {
            return new ToolExecutionResult("The sign-in has expired. Ask the user to sign in again.", true);
        }
        catch (McpToolUnavailableException)
        {
            return new ToolExecutionResult("The wallet service is temporarily unavailable. Tell the user to try again later.", true);
        }
    }

    public static TransferRequest? ParseRequest(JsonNode arguments, out string? problem)
    {
        problem = null;
        var network = (string?)arguments["network"];
        if (network is not ("mainnet" or "testnet"))
        {
            problem = "Only Algorand mainnet and testnet transfers are available. Bitcoin transfers are not supported yet.";
            return null;
        }
        var from = (string?)arguments["from"];
        var to = (string?)arguments["to"];
        if (!ValidAddress(from) || !ValidAddress(to))
        {
            problem = "The from and to values must be valid Algorand addresses.";
            return null;
        }
        ulong assetId = 0;
        if (arguments["assetId"] is { } a)
        {
            try
            {
                var raw = a.GetValue<long>();
                if (raw < 0) { problem = "Invalid asset ID."; return null; }
                assetId = (ulong)raw;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { problem = "Invalid asset ID."; return null; }
        }
        var amount = (string?)arguments["amount"];
        if (amount is null || !ulong.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amt) || amt == 0)
        {
            problem = "The amount must be a positive whole number of base units.";
            return null;
        }
        return new TransferRequest(network, from!, to!, assetId, amt.ToString(CultureInfo.InvariantCulture));
    }

    private static bool ValidAddress(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        try { _ = new Address(s); return true; }
        catch (Exception) { return false; }
    }
}

/// <summary>Maps a transfer preview to the card the widget renders: every planned step, numbered, built or not.</summary>
public static class TransferCard
{
    public static Contracts.ActionPreview Map(TransferOutcome o)
    {
        var snap = o.Snapshot!;
        var r = o.Request!;
        var built = snap.Steps.OrderBy(s => s.Seq).ToList();
        var steps = new List<Contracts.ActionStep>();
        for (var i = 0; i < o.Plan.Count; i++)
        {
            var plan = o.Plan[i];
            var title = $"Step {i + 1} of {o.Plan.Count}: {plan.Title}";
            var symbol = plan.AssetId == 0 ? "ALGO" : o.Symbol;
            var decimals = plan.AssetId == 0 ? 6 : o.Decimals;
            var assets = plan.Kind == TransferStepKind.OptIn ? new List<AssetAmount>() :
            [
                new AssetAmount { AssetId = plan.AssetId == 0 ? "ALGO" : plan.AssetId.ToString(CultureInfo.InvariantCulture), Symbol = symbol, Amount = plan.Amount, Decimals = decimals },
            ];
            if (i < built.Count)
            {
                var s = built[i];
                steps.Add(new Contracts.ActionStep
                {
                    StepId = s.Id.ToString(), Index = i, Title = title, Status = StepState(s.Status), GroupHash = s.SubjectHash,
                    From = plan.Sender, To = plan.Receiver, Assets = assets, FeeAmount = Fee(s),
                    SimulationSummary = i == built.Count - 1 ? o.Simulation : null,
                    TransactionId = s.TxIds.Length > 0 ? s.TxIds[0] : null,
                });
            }
            else
            {
                steps.Add(new Contracts.ActionStep
                {
                    StepId = $"{snap.Preview.Id}:{i}", Index = i, Title = title, Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
                    GroupHash = "", From = plan.Sender, To = plan.Receiver, Assets = assets,
                    SimulationSummary = "Built after the previous step lands.",
                });
            }
        }

        var network = McpNetworks.Algorand(r.Network);
        var amountText = TransferPlanner.FormatAmount(r.Amount, o.Decimals);
        var genesis = r.Network == "mainnet" ? "mainnet-v1.0" : "testnet-v1.0";
        return new Contracts.ActionPreview
        {
            PreviewId = snap.Preview.Id.ToString(),
            Capability = "wallet.transfer",
            Intent = $"Send {amountText} {o.Symbol} from {Short(r.From)} to {Short(r.To)}" + (o.Plan.Count > 1 ? $" ({o.Plan.Count} steps)" : ""),
            Network = network,
            GenesisId = genesis,
            Status = PreviewState(snap.Preview.Status),
            Steps = steps,
            ExpectsAuthenticatorApproval = snap.Preview.PredictedNeedsConsent,
            ExpiresAt = snap.Preview.ExpiresAt,
        };
    }

    private static string Short(string a) => a.Length <= 12 ? a : a[..6] + "..." + a[^6..];

    private static string? Fee(CoreStep s)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(s.UnsignedGroup ?? "[]") ?? [];
            return list.Count == 0 ? null : list.Select(t => Algorand.Algod.Model.Transactions.Transaction.FromBase64String(t).Fee)
                .Aggregate(0UL, (x, y) => x + y).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
    }

    internal static Contracts.ActionPreviewStatus StepState(StepStatus s) => s switch
    {
        StepStatus.Pending => Contracts.ActionPreviewStatus.AwaitingConfirmation,
        StepStatus.AwaitingConsent => Contracts.ActionPreviewStatus.AwaitingApproval,
        StepStatus.Signed => Contracts.ActionPreviewStatus.Signing,
        StepStatus.Submitted => Contracts.ActionPreviewStatus.Submitted,
        StepStatus.Confirmed => Contracts.ActionPreviewStatus.Completed,
        StepStatus.Failed => Contracts.ActionPreviewStatus.Failed,
        StepStatus.Expired => Contracts.ActionPreviewStatus.Expired,
        _ => Contracts.ActionPreviewStatus.Cancelled,
    };

    internal static Contracts.ActionPreviewStatus PreviewState(CoreStatus s) => s switch
    {
        CoreStatus.Proposed => Contracts.ActionPreviewStatus.AwaitingConfirmation,
        CoreStatus.Confirmed => Contracts.ActionPreviewStatus.Signing,
        CoreStatus.InProgress => Contracts.ActionPreviewStatus.Submitted,
        CoreStatus.Completed => Contracts.ActionPreviewStatus.Completed,
        CoreStatus.Failed => Contracts.ActionPreviewStatus.Failed,
        CoreStatus.Expired => Contracts.ActionPreviewStatus.Expired,
        _ => Contracts.ActionPreviewStatus.Cancelled,
    };
}

public static class WalletTransferRegistration
{
    /// <summary>Registers the wallet.transfer executor. Needs AddBiatecMcp and AddConfirm (for <see cref="ActionPreviewService"/>).</summary>
    public static IServiceCollection AddWalletTransferTools(this IServiceCollection services)
    {
        // One algod simulate client per network, from the Algod:Networks configuration (keyed "algorand-<name>").
        services.TryAddSingleton(sp =>
        {
            var nets = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Biatec.Chat.Api.Config.AlgodOptions>>().Value.Networks
                .ToDictionary(n => "algorand-" + n.Key, n => new Biatec.Chat.Tools.Algod.AlgodNetworkOptions(n.Value.Url, n.Value.Token), StringComparer.OrdinalIgnoreCase);
            return new Biatec.Chat.Tools.Algod.AlgodClientFactory(nets);
        });
        services.AddScoped<TransferPreviewService>();
        services.AddScoped<BitcoinTransferService>();
        services.AddOptions<BitcoinOptions>().BindConfiguration(BitcoinOptions.Section);
        services.AddHttpClient<IBitcoinChainWatcher, EsploraBitcoinChainWatcher>(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<Biatec.Chat.Api.Chat.Confirm.IPreviewFreshnessGate, BitcoinPreviewFreshnessGate>();
        services.AddSingleton<IToolExecutor, WalletTransferExecutor>();
        return services;
    }
}
