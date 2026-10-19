using System.ComponentModel.DataAnnotations;

namespace Biatec.Chat.Api.Config;

/// <summary>Biatec OIDC issuer and the MCP resource indicator tokens are minted for.</summary>
public sealed class OidcOptions
{
    public const string Section = "Oidc";

    [Required, Url] public string Issuer { get; set; } = "";
    [Required, Url] public string McpResource { get; set; } = "";
}

public sealed class McpOptions
{
    public const string Section = "Mcp";

    [Required, Url] public string Endpoint { get; set; } = "";
}

public sealed class BedrockOptions
{
    public const string Section = "Bedrock";

    /// <summary>All AWS calls stay in eu-central-1 (hackathon rule).</summary>
    [Required, RegularExpression("^eu-central-1$", ErrorMessage = "Bedrock:Region must be eu-central-1.")]
    public string Region { get; set; } = "eu-central-1";

    /// <summary>The cost-saving model for anonymous capability-discovery sessions (Sonnet generation).</summary>
    [Required, RegularExpression(@"^eu\.anthropic\.claude-(haiku|sonnet|opus)-4-5-.+", ErrorMessage = "Bedrock:AnonymousModelId must be an eu.anthropic.claude-*-4-5 inference profile id (Claude 5 isn't available on this account).")]
    public string AnonymousModelId { get; set; } = "eu.anthropic.claude-sonnet-4-5-20250929-v1:0";

    /// <summary>The default model for signed-in sessions (current Opus generation available on this account).</summary>
    [Required, RegularExpression(@"^eu\.anthropic\.claude-(haiku|sonnet|opus)-4-5-.+", ErrorMessage = "Bedrock:AuthenticatedModelId must be an eu.anthropic.claude-*-4-5 inference profile id (Claude 5 isn't available on this account).")]
    public string AuthenticatedModelId { get; set; } = "eu.anthropic.claude-opus-4-5-20251101-v1:0";

    [Range(1, 64000)] public int MaxTokens { get; set; } = 4096;
}

public sealed class RedisOptions
{
    public const string Section = "Redis";

    [Required] public string ConnectionString { get; set; } = "";
}

public sealed class PostgresOptions
{
    public const string Section = "Postgres";

    [Required] public string ConnectionString { get; set; } = "";
}

public sealed class AlgodNetworkOptions
{
    [Required, Url] public string Url { get; set; } = "";

    /// <summary>Optional API token. Supply it only through env vars or user-secrets.</summary>
    public string? Token { get; set; }
}

public sealed class AlgodOptions
{
    public const string Section = "Algod";

    /// <summary>Algod endpoint per network name (for example "mainnet", "testnet").</summary>
    public Dictionary<string, AlgodNetworkOptions> Networks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class KmsOptions
{
    public const string Section = "Kms";

    [Required, RegularExpression("^eu-central-1$", ErrorMessage = "Kms:Region must be eu-central-1.")]
    public string Region { get; set; } = "eu-central-1";

    /// <summary>Key id or alias used to wrap vault data keys.</summary>
    [Required] public string KeyId { get; set; } = "";
}
