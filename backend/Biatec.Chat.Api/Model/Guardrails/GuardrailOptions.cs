using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Biatec.Chat.Api.Model.Guardrails;

/// <summary>
/// Bedrock Guardrails settings (<c>Bedrock:Guardrail</c>). The guardrail itself is created by
/// <c>infra/guardrail/create.sh</c>; this only references its id and numbered version. It is a defence layer on top of,
/// never instead of, the server-side tier and scope gates.
/// </summary>
public sealed class GuardrailOptions : IValidatableObject
{
    public const string Section = "Bedrock:Guardrail";

    /// <summary>Off by default: the guardrail only exists after the create script ran in the AWS account.</summary>
    public bool Enabled { get; set; }

    public string GuardrailId { get; set; } = "";

    /// <summary>A published, numbered version. DRAFT is refused because it changes under the running service.</summary>
    public string GuardrailVersion { get; set; } = "";

    /// <summary>Tool results are screened in blocks of this many characters (one ApplyGuardrail text unit is 1000).</summary>
    [Range(1000, 100000)] public int ScreenBlockChars { get; set; } = 20000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled) yield break;
        if (string.IsNullOrWhiteSpace(GuardrailId))
            yield return new ValidationResult("Bedrock:Guardrail:GuardrailId is required when the guardrail is enabled.", [nameof(GuardrailId)]);
        if (!Regex.IsMatch(GuardrailVersion ?? "", "^[1-9][0-9]*$"))
            yield return new ValidationResult("Bedrock:Guardrail:GuardrailVersion must be a numbered version (not DRAFT).", [nameof(GuardrailVersion)]);
    }
}
