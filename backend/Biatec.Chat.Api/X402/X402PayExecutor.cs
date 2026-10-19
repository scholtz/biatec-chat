using System.Globalization;
using System.Text.Json.Nodes;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Wallet.Read;
using Biatec.Chat.Core.Canonical;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Builders;
using Biatec.Chat.Tools.Mcp;
using Biatec.Chat.Tools.X402;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Api.X402;

/// <summary>
/// <c>x402.pay</c>: builds the USDC transfer for an x402-protected resource and shows it as a preview. The preview's amount
/// is what the built group decodes to, not the seller's advertised price. Nothing is signed or paid here: the user confirms
/// the preview, the group goes to Biatec OIDC for signing, and only then does the facilitator settle.
/// Only registered when the platform flag <c>X402:Enabled</c> is on; the tool gate additionally needs the tenant's x402Enabled.
/// </summary>
public sealed class X402PayExecutor(IServiceScopeFactory scopes, IBiatecMcpClient mcp, TimeProvider clock) : IToolExecutor
{
    public const int UsdcDecimals = 6;
    /// <summary>Roughly the block time, used to turn the group's round window into a wall-clock expiry.</summary>
    private const double SecondsPerRound = 2.8;
    private static readonly TimeSpan MaxStepLifetime = TimeSpan.FromMinutes(30);

    public string CapabilityName => "x402.pay";
    public string? StatusLabel(JsonNode arguments) => "Preparing the x402 payment";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct)
    {
        // Defence in depth: the gate already hid the capability unless the tenant flag was on.
        if (context.Tier != Core.Data.SessionTier.Authenticated || context.Tenant?.X402Enabled != true)
            return new ToolExecutionResult("x402 payments are not available.", true);

        var network = arguments["network"]!.GetValue<string>();
        var from = arguments["from"]!.GetValue<string>();
        var resourceUrl = arguments["resourceUrl"]!.GetValue<string>();

        var own = await mcp.ListAlgorandAddressesAsync(new McpCallContext(context.TenantId, context.SessionId, context.Scopes), ct);
        if (own.IsError) return new ToolExecutionResult("The addresses could not be read. Tell the user to try again.", true);
        if (!WalletReadExecutor.ParseAddresses(own.Json).Contains(from, StringComparer.Ordinal))
            return new ToolExecutionResult("That address does not belong to the signed-in user. Use wallet.listAddresses to see the user's addresses.", true);

        using var scope = scopes.CreateScope();
        var pay = scope.ServiceProvider.GetRequiredService<X402PayService>();
        X402Preparation prep;
        try { prep = await pay.PrepareAsync(network, from, resourceUrl, ct); }
        catch (X402PolicyException e) { return new ToolExecutionResult($"The resource cannot be used: {e.Code}. {e.Message}", true); }
        catch (X402Exception e) { return new ToolExecutionResult($"The payment could not be prepared: {e.Code}. {e.Message}", true); }

        var t = prep.Transfer;
        var amount = FormatUsdc(t.Amount);
        var (_, lastValid) = prep.Group.ValidityWindow();
        var window = TimeSpan.FromSeconds((lastValid - prep.Group.Transactions.Min(x => x.FirstValid)) * SecondsPerRound);
        var expires = clock.GetUtcNow() + (window < MaxStepLifetime ? window : MaxStepLifetime);

        var canonical = JsonCanonicalizer.Canonicalize(new JsonObject
        {
            ["capability"] = CapabilityName,
            ["network"] = network,
            ["resourceUrl"] = prep.ResourceUrl,
            ["from"] = t.Sender,
            ["payTo"] = t.Receiver,
            ["assetId"] = t.AssetId.ToString(CultureInfo.InvariantCulture),
            ["amount"] = t.Amount.ToString(CultureInfo.InvariantCulture),
            ["groupHash"] = prep.Group.GroupHash,
        });

        var previews = scope.ServiceProvider.GetRequiredService<ActionPreviewService>();
        var snapshot = await previews.CreateAsync(new NewPreviewSpec(
            context.ConversationId, CapabilityName, canonical,
            [new NewStepSpec(ActionStepKind.Sign, prep.Group.GroupHash, expires, UnsignedGroup: new JsonArray(prep.Group.ToBase64().Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()).ToJsonString(),
                SenderAddress: t.Sender, Network: network, FirstValid: (long)prep.Group.Transactions.Min(x => x.FirstValid), LastValid: (long)lastValid)]), ct);

        var intent = $"Pay {amount} USDC to {t.Receiver} for {prep.ResourceUrl}";
        var card = new Contracts.ActionPreview
        {
            PreviewId = snapshot.Preview.Id.ToString(),
            Capability = CapabilityName,
            Intent = intent,
            Network = network,
            GenesisId = prep.Group.Transactions[0].GenesisId ?? "",
            Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
            ExpiresAt = snapshot.Preview.ExpiresAt,
            Steps =
            [
                new Contracts.ActionStep
                {
                    StepId = snapshot.Steps[0].Id.ToString(),
                    Index = 0,
                    Title = "Pay for the resource",
                    Status = Contracts.ActionPreviewStatus.AwaitingConfirmation,
                    GroupHash = prep.Group.GroupHash,
                    From = t.Sender,
                    To = t.Receiver,
                    Assets = [new AssetAmount { AssetId = t.AssetId.ToString(CultureInfo.InvariantCulture), Symbol = "USDC", Amount = t.Amount.ToString(CultureInfo.InvariantCulture), Decimals = UsdcDecimals }],
                    FeeAmount = prep.Group.Transactions.Sum(x => (decimal)x.Fee).ToString(CultureInfo.InvariantCulture),
                    SimulationSummary = $"Decoded from the built transaction: {amount} USDC from {t.Sender} to {t.Receiver}.",
                },
            ],
            ExpectsAuthenticatorApproval = false,
        };

        var content = new JsonObject
        {
            ["previewId"] = card.PreviewId,
            ["status"] = "awaiting_confirmation",
            ["intent"] = intent,
            ["amountUsdc"] = amount,
            ["note"] = "The user must confirm the preview card. Nothing has been paid.",
        };
        return new ToolExecutionResult(content.ToJsonString(), false, [new PreviewCardEvent(card)]);
    }

    internal static string FormatUsdc(ulong baseUnits)
    {
        var whole = baseUnits / 1_000_000UL;
        var frac = baseUnits % 1_000_000UL;
        return frac == 0 ? whole.ToString(CultureInfo.InvariantCulture) : $"{whole}.{frac:D6}".TrimEnd('0');
    }
}

public static class X402Registration
{
    /// <summary>
    /// Registers the x402 executor only when <c>X402:Enabled</c> is true. With the flag off (the default) nothing is
    /// registered, so no x402 client, policy or executor exists and the capability stays unreachable.
    /// </summary>
    public static IServiceCollection AddX402Pay(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>($"{X402Options.Section}:Enabled")) return services;
        services.AddBuildersClient(configuration);
        services.AddX402(configuration);
        services.AddSingleton<IToolExecutor, X402PayExecutor>();
        return services;
    }
}
