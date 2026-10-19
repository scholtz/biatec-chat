using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Model.Guardrails;

/// <summary>Outcome of screening untrusted text before the model sees it.</summary>
public sealed record GuardrailVerdict(bool Withheld)
{
    public static readonly GuardrailVerdict Allowed = new(false);
    public static readonly GuardrailVerdict Blocked = new(true);
}

/// <summary>Screens tool results (attacker-controlled on-chain data, web content) for injected instructions.</summary>
public interface IToolResultGuard
{
    Task<GuardrailVerdict> ScreenAsync(string source, string text, CancellationToken ct);
}

/// <summary>What the model sees instead of a withheld tool result.</summary>
public static class GuardrailMessages
{
    public const string ToolResultWithheld = "Tool output withheld: it contained instructions or unsafe content. Tell the user the result could not be used.";
}

/// <summary>
/// Applies the configured guardrail to a tool result with the standalone ApplyGuardrail API (source INPUT, since the
/// text is input to the model). Fails closed: if the guardrail can't be reached the result is withheld, because the
/// content is untrusted. A no-op when the feature is disabled.
/// </summary>
public sealed class BedrockToolResultGuard(
    IAmazonBedrockRuntime bedrock,
    IOptions<GuardrailOptions> options,
    ILogger<BedrockToolResultGuard> log) : IToolResultGuard
{
    private readonly GuardrailOptions _o = options.Value;

    public async Task<GuardrailVerdict> ScreenAsync(string source, string text, CancellationToken ct)
    {
        if (!_o.Enabled || string.IsNullOrEmpty(text)) return GuardrailVerdict.Allowed;
        try
        {
            var blocks = new List<GuardrailContentBlock>();
            for (var i = 0; i < text.Length; i += _o.ScreenBlockChars)
                blocks.Add(new GuardrailContentBlock { Text = new GuardrailTextBlock { Text = text.Substring(i, Math.Min(_o.ScreenBlockChars, text.Length - i)) } });

            var response = await bedrock.ApplyGuardrailAsync(new ApplyGuardrailRequest
            {
                GuardrailIdentifier = _o.GuardrailId,
                GuardrailVersion = _o.GuardrailVersion,
                Source = GuardrailContentSource.INPUT,
                Content = blocks,
            }, ct);

            if (response.Action == GuardrailAction.GUARDRAIL_INTERVENED)
            {
                log.LogWarning("Guardrail withheld a result of {Source} ({Chars} chars)", source, text.Length);
                return GuardrailVerdict.Blocked;
            }
            return GuardrailVerdict.Allowed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Guardrail check for {Source} failed ({ErrorType}); the result is withheld", source, ex.GetType().Name);
            return GuardrailVerdict.Blocked;
        }
    }
}

public static class GuardrailRegistration
{
    public static IServiceCollection AddBedrockGuardrails(this IServiceCollection services)
    {
        services.AddOptions<GuardrailOptions>()
            .BindConfiguration(GuardrailOptions.Section)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<IToolResultGuard, BedrockToolResultGuard>();
        return services;
    }
}
