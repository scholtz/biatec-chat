using System.ComponentModel.DataAnnotations;
using Biatec.Chat.Api.Config;

namespace Biatec.Chat.Api.Model;

/// <summary>Throughput settings of the Bedrock client, bound from the same <c>Bedrock</c> section as <see cref="BedrockOptions"/>.</summary>
public sealed class BedrockClientOptions : IValidatableObject
{
    /// <summary>Process-wide cap on concurrent Bedrock calls; the account's Claude rate limit is shared by the team.</summary>
    [Range(1, 256)] public int MaxConcurrency { get; set; } = 8;

    /// <summary>How long a call may wait for free slots in total, retries included, before <see cref="ModelBusyException"/>.</summary>
    [Range(1, 600000)] public int QueueTimeoutMs { get; set; } = 30000;

    /// <summary>Longest wait for the first stream event: connecting plus the model's time to first token.</summary>
    [Range(1000, 600000)] public int FirstEventTimeoutMs { get; set; } = 180000;

    /// <summary>Longest gap between two stream events before the message stop; a stalled stream frees its slot.</summary>
    [Range(1000, 600000)] public int StreamIdleTimeoutMs { get; set; } = 60000;

    /// <summary>Retries after a throttled or transiently failed call, with exponential backoff and equal jitter.</summary>
    [Range(0, 10)] public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// Bedrock quotas are per minute and shared by the team, so the defaults spread retries over about half a
    /// minute (bounds 2, 4, 8 and 16 s) rather than retrying straight back into an exhausted window.
    /// </summary>
    [Range(1, 60000)] public int RetryBaseDelayMs { get; set; } = 2000;

    [Range(1, 120000)] public int RetryMaxDelayMs { get; set; } = 30000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RetryMaxDelayMs < RetryBaseDelayMs)
            yield return new ValidationResult("Bedrock:RetryMaxDelayMs must be at least Bedrock:RetryBaseDelayMs.", [nameof(RetryMaxDelayMs)]);
    }
}

/// <summary>
/// Every concurrency slot stayed taken for the whole queue budget; the caller should answer "busy, try again".
/// When the wait was for a retry, the error that caused the retry is the inner exception.
/// </summary>
public sealed class ModelBusyException(TimeSpan budget, Exception? retryCause = null)
    : Exception($"No Bedrock slot became free within {budget.TotalSeconds:0.#} s.", retryCause);

/// <summary>The event stream ended before the message stop: the answer is truncated.</summary>
public sealed class ModelStreamIncompleteException(string modelId, string reason = "ended")
    : IOException($"The Bedrock stream for {modelId} {reason} before the message stop.");
