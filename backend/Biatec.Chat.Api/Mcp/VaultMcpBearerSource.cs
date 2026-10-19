using Biatec.Chat.Api.Vault;
using Biatec.Chat.Tools.Mcp;

namespace Biatec.Chat.Api.Mcp;

/// <summary>The MCP bearer is the session's access token from the vault, refreshed on demand. It never reaches the browser.</summary>
public sealed class VaultMcpBearerSource(ITokenVault vault) : IMcpBearerSource
{
    public Task<string?> GetBearerAsync(string sessionId, CancellationToken ct) => vault.GetAccessTokenAsync(sessionId, ct);
}
