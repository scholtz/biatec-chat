using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Model.Guardrails;

/// <summary>Outcome of screening untrusted text before the model sees it. <see cref="CheckFailed"/>: withheld because the check itself failed.</summary>
public sealed record GuardrailVerdict(bool Withheld, bool CheckFailed = false)
{
    public static readonly GuardrailVerdict Allowed = new(false);
    public static readonly GuardrailVerdict Blocked = new(true);
    public static readonly GuardrailVerdict Unavailable = new(true, true);

    /// <summary>What the model sees instead of a withheld result.</summary>
    public string ModelMessage => CheckFailed ? GuardrailMessages.ToolResultUnchecked : GuardrailMessages.ToolResultWithheld;
}

/// <summary>Screens tool results (attacker-controlled on-chain data, web content) for injected instructions.</summary>
public interface IToolResultGuard
{
    Task<GuardrailVerdict> ScreenAsync(string source, string text, CancellationToken ct);
}

/// <summary>What the model sees instead of a withheld tool result.</summary>
public static class GuardrailMessages
{
    public const string ToolResultWithheld = "Tool output withheld: a safety check found instructions or unsafe content in it. Tell the user a safety check stopped this result; do not describe it as a data or connection problem, and do not retry the same call.";

    public const string ToolResultUnchecked = "Tool output withheld: the safety check for tool output could not run, so this result was not used. Tell the user the result could not be checked and to try again later, and that the Biatec team should be told if it keeps happening.";
}

/// <summary>
/// Applies the configured guardrail to a tool result with the standalone ApplyGuardrail API (source INPUT, since the
/// text is input to the model). Fails closed: if the guardrail can't be reached the result is withheld, because the
/// content is untrusted. A no-op when the feature is disabled.
/// Some denied topics (InvestmentAdvice) are about what the assistant says, not about data: prices, pools and balances
/// match them. An intervention whose only reasons are topics listed in <see cref="GuardrailOptions.ToolResultIgnoredTopics"/>
/// lets the result through; any other finding (prompt attack, harmful content, recovery phrase, other topics) withholds it.
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

            if (response.Action != GuardrailAction.GUARDRAIL_INTERVENED) return GuardrailVerdict.Allowed;

            var reasons = Reasons(response.Assessments);
            if (OnlyTopicFindings(response.Assessments) && reasons.Count > 0 && reasons.All(r => _o.ToolResultIgnoredTopics.Any(t => r == "topic:" + t)))
            {
                log.LogInformation("Guardrail matched only ignored topics on {Source} ({Reasons}); data is not advice, so the result is kept", source, string.Join(", ", reasons));
                return GuardrailVerdict.Allowed;
            }
            log.LogWarning("Guardrail withheld a result of {Source} ({Chars} chars): {Reasons}", source, text.Length,
                reasons.Count == 0 ? "no assessment" : string.Join(", ", reasons));
            return GuardrailVerdict.Blocked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // AWS error messages name the request problem (permissions, throttling, validation), never the screened text.
            log.LogWarning("Guardrail check for {Source} failed ({ErrorType}: {Error}); the result is withheld", source, ex.GetType().Name, ex.Message);
            return GuardrailVerdict.Unavailable;
        }
    }

    /// <summary>
    /// True when no assessment reports anything but topics. Fails closed: a policy <see cref="Reasons"/> doesn't read
    /// (contextual grounding, automated reasoning, a masked PII entity) means the result is not a topic-only match.
    /// </summary>
    public static bool OnlyTopicFindings(List<GuardrailAssessment>? assessments) =>
        (assessments ?? []).All(a =>
            a.ContentPolicy?.Filters is null or [] &&
            a.SensitiveInformationPolicy?.PiiEntities is null or [] &&
            a.SensitiveInformationPolicy?.Regexes is null or [] &&
            a.WordPolicy?.CustomWords is null or [] &&
            a.WordPolicy?.ManagedWordLists is null or [] &&
            a.ContextualGroundingPolicy?.Filters is null or [] &&
            a.AutomatedReasoningPolicy?.Findings is null or []);

    /// <summary>The blocking findings, as "content:PROMPT_ATTACK/LOW", "topic:InvestmentAdvice", "pii:…", "regex:…" or "word:…".</summary>
    public static List<string> Reasons(List<GuardrailAssessment>? assessments)
    {
        var reasons = new List<string>();
        foreach (var a in assessments ?? [])
        {
            reasons.AddRange(from f in a.ContentPolicy?.Filters ?? [] where f.Action == GuardrailContentPolicyAction.BLOCKED select $"content:{f.Type}/{f.Confidence}");
            reasons.AddRange(from t in a.TopicPolicy?.Topics ?? [] where t.Action == GuardrailTopicPolicyAction.BLOCKED select $"topic:{t.Name}");
            reasons.AddRange(from e in a.SensitiveInformationPolicy?.PiiEntities ?? [] where e.Action == GuardrailSensitiveInformationPolicyAction.BLOCKED select $"pii:{e.Type}");
            reasons.AddRange(from r in a.SensitiveInformationPolicy?.Regexes ?? [] where r.Action == GuardrailSensitiveInformationPolicyAction.BLOCKED select $"regex:{r.Name}");
            reasons.AddRange(from w in a.WordPolicy?.CustomWords ?? [] where w.Action == GuardrailWordPolicyAction.BLOCKED select "word:custom");
            reasons.AddRange(from w in a.WordPolicy?.ManagedWordLists ?? [] where w.Action == GuardrailWordPolicyAction.BLOCKED select $"word:{w.Type}");
        }
        return reasons;
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
