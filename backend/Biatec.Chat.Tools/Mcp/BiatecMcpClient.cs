using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tools.Mcp;

/// <summary>Typed access to the Biatec MCP tools. There is deliberately no method that signs and no generic call-by-name.</summary>
public interface IBiatecMcpClient
{
    /// <summary>The tools offered to this tenant and scope set (lazy, cached; signTransaction is never listed).</summary>
    Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default);

    Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default);
    Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default);

    /// <summary><c>getCryptoAddress</c> for one ARC-76 derivation slot of the primary seed: 0 is the user's first (primary) address, 1 the second.</summary>
    Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, int slot, CancellationToken ct = default) =>
        slot == 0 ? GetCryptoAddressAsync(ctx, network, ct) : throw new NotSupportedException("This client cannot read other derivation slots.");

    Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default);
    Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default);
    Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default);
    Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default);
    Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default);
    Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default);
}

public sealed class BiatecMcpClient(
    IMcpConnectionFactory connections,
    IMcpBearerSource bearers,
    McpToolCache cache,
    IMcpAlertSink alerts,
    IOptions<McpOptions> options,
    ILogger<BiatecMcpClient> log) : IBiatecMcpClient
{
    private readonly McpOptions _o = options.Value;

    public async Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpCallContext ctx, CancellationToken ct = default)
    {
        var cached = cache.TryGet(ctx, _o.CacheTtl);
        if (cached is not null) return cached;
        await using var conn = await ConnectAsync(ctx, ct);
        return await RefreshAsync(conn, ctx, ct);
    }

    private async Task<IMcpConnection> ConnectAsync(McpCallContext ctx, CancellationToken ct)
    {
        var bearer = await bearers.GetBearerAsync(ctx.SessionId, ct);
        if (string.IsNullOrEmpty(bearer)) throw new McpAuthenticationException(ctx.SessionId);
        return await connections.ConnectAsync(bearer, ct);
    }

    private async Task<IReadOnlyList<McpToolInfo>> RefreshAsync(IMcpConnection conn, McpCallContext ctx, CancellationToken ct)
    {
        var tools = (await conn.ListToolsAsync(ct)).Where(t => !McpToolNames.IsBlocked(t.Name)).ToList();
        cache.Put(ctx, tools);
        foreach (var required in McpToolNames.Required)
        {
            if (tools.Any(t => t.Name == required)) continue;
            log.LogError("Biatec MCP no longer offers required tool {Tool} (tenant {Tenant}, scopes {Scopes}).", required, ctx.TenantId, ctx.ScopeKey);
            alerts.RequiredToolMissing(ctx, required);
        }
        return tools;
    }

    private async Task<McpToolResult> CallAsync(McpCallContext ctx, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        if (McpToolNames.IsBlocked(tool)) throw new McpToolBlockedException(tool);
        await using var conn = await ConnectAsync(ctx, ct);
        // Piggyback: the list is refreshed on this authenticated call once stale, never on a timer.
        var cached = cache.TryGet(ctx, _o.CacheTtl);
        var tools = cached ?? await RefreshAsync(conn, ctx, ct);
        if (cached is not null && tools.All(t => t.Name != tool))
        {
            // A cached list lacking the tool may be stale (the tool can have reappeared): one forced refresh before giving up.
            log.LogWarning("Cached Biatec MCP tool list lacks {Tool} (tenant {Tenant}); refreshing once.", tool, ctx.TenantId);
            tools = await RefreshAsync(conn, ctx, ct);
        }
        if (tools.All(t => t.Name != tool))
        {
            log.LogError("Biatec MCP does not offer tool {Tool} (tenant {Tenant}, scopes {Scopes}).", tool, ctx.TenantId, ctx.ScopeKey);
            throw new McpToolUnavailableException(tool);
        }
        return await conn.CallToolAsync(tool, args, ct);
    }

    public Task<McpToolResult> ListAlgorandAddressesAsync(McpCallContext ctx, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.ListAlgorandAddresses, new(), ct);

    public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.GetCryptoAddress, new() { ["network"] = network }, ct);

    public Task<McpToolResult> GetCryptoAddressAsync(McpCallContext ctx, string network, int slot, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.GetCryptoAddress, new() { ["network"] = network, ["slot"] = slot }, ct);

    public Task<McpToolResult> GetCryptoBalanceAsync(McpCallContext ctx, string network, string address, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.GetCryptoBalance, new() { ["network"] = network, ["address"] = address }, ct);

    public Task<McpToolResult> CreatePaymentTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amount, ulong? assetId = null, string? note = null, CancellationToken ct = default)
    {
        var a = new Dictionary<string, object?> { ["network"] = network, ["sender"] = sender, ["receiver"] = receiver, ["amount"] = amount };
        if (assetId is { } id) a["assetId"] = id;
        if (note is not null) a["note"] = note;
        return CallAsync(ctx, McpToolNames.CreatePaymentTransaction, a, ct);
    }

    public Task<McpToolResult> CreateOptInTransactionAsync(McpCallContext ctx, string network, string address, ulong assetId, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.CreateOptInTransaction, new() { ["network"] = network, ["address"] = address, ["assetId"] = assetId }, ct);

    public Task<McpToolResult> CreateBitcoinTransactionAsync(McpCallContext ctx, string network, string sender, string receiver, string amountBtc, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.CreateBitcoinTransaction, new() { ["network"] = network, ["sender"] = sender, ["receiver"] = receiver, ["amount"] = amountBtc }, ct);

    public Task<McpToolResult> CreateSwapTransactionAsync(McpCallContext ctx, string network, string address, ulong fromAssetId, ulong toAssetId, string amount, string? slippage = null, CancellationToken ct = default)
    {
        var a = new Dictionary<string, object?> { ["network"] = network, ["address"] = address, ["fromAssetId"] = fromAssetId, ["toAssetId"] = toAssetId, ["amount"] = amount };
        if (slippage is not null) a["slippage"] = slippage;
        return CallAsync(ctx, McpToolNames.CreateSwapTransaction, a, ct);
    }

    public Task<McpToolResult> SubmitTransactionToBlockchainAsync(McpCallContext ctx, string network, string signedTransactionBase64, CancellationToken ct = default) =>
        CallAsync(ctx, McpToolNames.SubmitTransactionToBlockchain, new() { ["network"] = network, ["signedTransaction"] = signedTransactionBase64 }, ct);
}
