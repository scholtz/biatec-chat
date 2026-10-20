using System.Text.Json;
using ModelContextProtocol.Client;

namespace Biatec.Chat.Tools.Mcp;

/// <summary>Biatec MCP tool names this backend depends on, and the one it must never call.</summary>
public static class McpToolNames
{
    public const string ListAlgorandAddresses = "listAlgorandAddresses";
    public const string GetCryptoAddress = "getCryptoAddress";
    public const string GetCryptoBalance = "getCryptoBalance";
    public const string CreatePaymentTransaction = "createPaymentTransaction";
    public const string CreateOptInTransaction = "createOptInTransaction";
    public const string CreateBitcoinTransaction = "createBitcoinTransaction";
    public const string CreateSwapTransaction = "createSwapTransaction";
    public const string SubmitTransactionToBlockchain = "submitTransactionToBlockchain";

    /// <summary>Signing goes straight to Biatec OIDC so the step-up challenge reaches the backend (spec: tool executors).</summary>
    public const string SignTransaction = "signTransaction";

    public static readonly IReadOnlyList<string> Required =
    [
        ListAlgorandAddresses, GetCryptoAddress, GetCryptoBalance, CreatePaymentTransaction,
        CreateOptInTransaction, CreateBitcoinTransaction, CreateSwapTransaction, SubmitTransactionToBlockchain,
    ];

    public static bool IsBlocked(string name) => string.Equals(name?.Trim(), SignTransaction, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Biatec MCP network codes.</summary>
public static class McpNetworks
{
    /// <summary>The Biatec MCP code for a chat Algorand network name: <c>testnet</c> is <c>algorand-testnet</c>.</summary>
    public static string Algorand(string network) => "algorand-" + network;
}

public sealed class McpOptions
{
    public const string Section = "Mcp";
    public string Endpoint { get; set; } = "https://mcp.biatec.io/tools";

    /// <summary>The tool list is re-listed on the next authenticated call once older than this.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Who is calling: the tenant, the chat session whose vault holds the bearer, and the scopes of that session's token.</summary>
public sealed record McpCallContext(string TenantId, string SessionId, IReadOnlyCollection<string> Scopes)
{
    /// <summary>Order- and case-insensitive scope set key; "openid" and "openid sign" never collide.</summary>
    public string ScopeKey => string.Join(' ', Scopes.Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
}

public sealed record McpToolInfo(string Name, string? Description);

/// <summary>Result of one tool call. <see cref="Json"/> is the structured content, or the text content parsed as JSON when possible.</summary>
public sealed record McpToolResult(bool IsError, string Text, JsonElement? Json)
{
    /// <summary>
    /// A tool error, or a non-empty <c>error</c> field (a string, object or array; anything but null, false, "", {} or []):
    /// Biatec MCP reports most failures that way rather than as a tool error.
    /// </summary>
    public bool Failed
    {
        get
        {
            if (IsError) return true;
            if (Json is not { ValueKind: JsonValueKind.Object } o) return false;
            foreach (var p in o.EnumerateObject())
                if (string.Equals(p.Name, "error", StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind switch
                    {
                        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
                        JsonValueKind.String => !string.IsNullOrWhiteSpace(p.Value.GetString()),
                        JsonValueKind.Object => p.Value.EnumerateObject().Any(),
                        JsonValueKind.Array => p.Value.GetArrayLength() > 0,
                        _ => true,
                    };
            return false;
        }
    }
}

/// <summary>Supplies the user's current access token for a session (vault, refreshed on demand). Null when there is none.</summary>
public interface IMcpBearerSource
{
    Task<string?> GetBearerAsync(string sessionId, CancellationToken ct);
}

/// <summary>Receives an alert when a tool a capability depends on is not offered by the server.</summary>
public interface IMcpAlertSink
{
    void RequiredToolMissing(McpCallContext context, string toolName);
}

public sealed class McpToolUnavailableException(string tool) : Exception($"Biatec MCP does not offer the required tool '{tool}'.")
{
    public string Tool { get; } = tool;
}

public sealed class McpToolBlockedException(string tool) : InvalidOperationException($"Biatec MCP tool '{tool}' must never be called by the backend.")
{
    public string Tool { get; } = tool;
}

public sealed class McpAuthenticationException(string sessionId) : Exception("No bearer token available for the MCP call.")
{
    public string SessionId { get; } = sessionId;
}

/// <summary>One open MCP connection, authenticated with one bearer.</summary>
public interface IMcpConnection : IAsyncDisposable
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct);
    Task<McpToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
}

public interface IMcpConnectionFactory
{
    Task<IMcpConnection> ConnectAsync(string bearer, CancellationToken ct);
}

/// <summary>ModelContextProtocol C# SDK connection factory. The default transport is streamable HTTP with the bearer header.</summary>
public sealed class SdkMcpConnectionFactory(Func<string, IClientTransport> transport) : IMcpConnectionFactory
{
    public SdkMcpConnectionFactory(McpOptions options) : this(bearer => new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(options.Endpoint),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
    })) { }

    public async Task<IMcpConnection> ConnectAsync(string bearer, CancellationToken ct) =>
        new SdkConnection(await McpClient.CreateAsync(transport(bearer), cancellationToken: ct));

    private sealed class SdkConnection(McpClient client) : IMcpConnection
    {
        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
            (await client.ListToolsAsync(cancellationToken: ct)).Select(t => new McpToolInfo(t.Name, t.Description)).ToList();

        public async Task<McpToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            var r = await client.CallToolAsync(name, arguments, cancellationToken: ct);
            var text = string.Concat(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
            JsonElement? json = null;
            if (r.StructuredContent is { } sc) json = sc.Clone();
            else if (!string.IsNullOrWhiteSpace(text))
            {
                try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
            }
            return new McpToolResult(r.IsError == true, text, json);
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
