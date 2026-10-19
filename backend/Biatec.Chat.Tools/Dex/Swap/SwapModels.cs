using Biatec.Chat.Core.Previews;
using Biatec.Chat.Tools.Mcp;

namespace Biatec.Chat.Tools.Dex.Swap;

public sealed class SwapOptions
{
    public const string Section = "Dex:Swap";

    /// <summary>Slippage applied to every swap in basis points (50 = 0.5 %). The model cannot change it.</summary>
    public int SlippageBps { get; set; } = 50;

    /// <summary>Highest slippage the executor accepts, from configuration or from a router quote (basis points).</summary>
    public int MaxSlippageBps { get; set; } = 500;
}

/// <summary>The validated <c>dex.swap</c> arguments plus who is calling. Amounts are base units as decimal strings.</summary>
public sealed record SwapRequest(
    McpCallContext Context,
    Guid ConversationId,
    string Network,
    string From,
    ulong FromAssetId,
    ulong ToAssetId,
    string Amount,
    ulong? PoolAppId = null);

public enum SwapRoute { BiatecRouter, BiatecPool }

/// <summary>A registered Biatec DEX pool as offered to the user. Fee and prices are the pool's 1e9-scaled integers.</summary>
public sealed record SwapPool(
    ulong AppId,
    ulong AssetA,
    ulong AssetB,
    string Fee,
    string FeePercent,
    string PriceMin,
    string PriceMax,
    string CurrentPrice,
    string Liquidity,
    int VerificationClass);

public sealed record SwapPoolExclusion(ulong AppId, int VerificationClass, string Reason);

public sealed record SwapPoolListing(IReadOnlyList<SwapPool> Pools, IReadOnlyList<SwapPoolExclusion> Excluded, int? AccountVerificationClass);

/// <summary>Everything the widget shows on the swap card (also stored, canonicalised, as the preview's CanonicalJson).</summary>
public sealed record SwapPreviewCard(
    SwapRoute Route,
    string Intent,
    string Network,
    string Sender,
    ulong FromAssetId,
    ulong ToAssetId,
    string FromAmount,
    string ExpectedReceived,
    string MinimumReceived,
    int SlippageBps,
    string? PriceImpact,
    SwapPool? Pool,
    string GroupHash,
    IReadOnlyList<string> TxIds,
    IReadOnlyList<string> Notes);

public abstract record SwapOutcome;

/// <summary>A built, grouped swap ready for <see cref="ActionPreviewService.CreateAsync"/>.</summary>
public sealed record SwapPreviewReady(NewPreviewSpec Spec, SwapPreviewCard Card) : SwapOutcome;

/// <summary>
/// Biatec Router did not win (or was unavailable) and no pool was named: the user picks one of these pools or
/// cancels. Nothing was built.
/// </summary>
public sealed record SwapPoolChoiceRequired(string Question, IReadOnlyList<SwapPool> Pools, IReadOnlyList<SwapPoolExclusion> Excluded, string Reason) : SwapOutcome;

/// <summary>The swap cannot proceed. <see cref="Message"/> is safe to show; it contains no builder or MCP text.</summary>
public sealed record SwapRefused(string Message) : SwapOutcome;
