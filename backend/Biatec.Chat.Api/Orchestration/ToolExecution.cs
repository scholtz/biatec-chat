using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Api.Contracts;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;

namespace Biatec.Chat.Api.Orchestration;

/// <summary>What an executor knows about the call it runs. The gate already allowed it.</summary>
public sealed record ToolExecutionContext(
    Capability Capability,
    string ToolUseId,
    Guid ConversationId,
    string SessionId,
    string TenantId,
    Biatec.Chat.Core.Data.SessionTier Tier,
    IReadOnlyCollection<string> Scopes,
    Biatec.Chat.Core.Data.Tenant? Tenant = null,
    bool SignInAvailable = true,
    string PathBase = "");

/// <summary>
/// Outcome of a tool run. <see cref="Content"/> is data for the model and is always screened and wrapped as untrusted
/// before it is sent; <see cref="Events"/> (preview cards, sign-in cards) go straight to the browser.
/// <see cref="Guidance"/> is the executor's own fixed instruction to the model (what to ask the user next). It must
/// never contain upstream data: it skips the tool-result guardrail, whose prompt-attack filter flags instructions, and
/// is sent after the untrusted wrapper.
/// </summary>
public sealed record ToolExecutionResult(string Content, bool IsError = false, IReadOnlyList<ChatEvent>? Events = null, string? Guidance = null);

/// <summary>Runs one capability. Later tasks (knowledge base, wallet, DEX, safes) register implementations.</summary>
public interface IToolExecutor
{
    /// <summary>The capability name this executor serves, for example <c>kb.search</c>.</summary>
    string CapabilityName { get; }

    /// <summary>Short progress line for the browser, for example "Searching the knowledge base". Null falls back to the capability name.</summary>
    string? StatusLabel(JsonNode arguments) => null;

    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct);
}

/// <summary>Hook applied to everything before it is stored in <c>Message</c>. The default scrubs bearer tokens and JWTs.</summary>
public interface IMessageRedactor
{
    /// <param name="toolName">Capability name for tool results, otherwise null.</param>
    string Redact(MessageRole role, string? toolName, string content);
}

public sealed partial class DefaultMessageRedactor : IMessageRedactor
{
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._~+/=-]{12,}")]
    private static partial Regex Bearer();

    public string Redact(MessageRole role, string? toolName, string content) =>
        Bearer().Replace(Jwt().Replace(content, "[redacted-token]"), "Bearer [redacted-token]");
}

/// <summary>Wraps content that does not come from the user so the model treats it as data.</summary>
public static class UntrustedData
{
    public static string Wrap(string source, string content)
    {
        // The content cannot close the wrapper early.
        var safe = content.Replace("</untrusted_data", "<\\/untrusted_data", StringComparison.OrdinalIgnoreCase);
        var src = new string(source.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(64).ToArray());
        return $"<untrusted_data source=\"{src}\">\n{safe}\n</untrusted_data>";
    }
}
